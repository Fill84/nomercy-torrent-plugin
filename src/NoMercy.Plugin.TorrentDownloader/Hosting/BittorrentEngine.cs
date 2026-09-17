using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The plugin's own torrent client, behind the port the pipeline sees.
/// </summary>
/// <remarks>
/// <para>
/// The protocol lives in the Bittorrent assembly, which references nothing;
/// this is the adapter that gives it the shape <c>Core</c> asks for. Every
/// torrent it holds is a <see cref="TorrentRun"/> that is really announcing,
/// really dialling and really writing to a disk — for a whole sprint this class
/// recorded a magnet and stopped, and everything built on the port was correct
/// against a client that never finished anything.
/// </para>
/// <para>
/// Started once and stopped once, whatever ticks in between: the client owns
/// sockets and a port mapping, and a second one would bind a port the first
/// already has and report it as somebody else's.
/// </para>
/// </remarks>
public sealed class BittorrentEngine(
    int listenPort,
    TimeSpan metadataTimeout,
    TimeSpan stallLimit,
    int maxConcurrent,
    SeedLimit seeding,
    long maxDownloadRate,
    long maxUploadRate,
    PortMapping? mapping,
    IActivityJournal journal,
    ILogger logger,
    ITrackerTransport transport,
    IPeerDialler dialler,
    TimeProvider? time = null,
    ResumeKeeper? resume = null,
    IStorageSpace? space = null,
    Func<TorrentMetadata, TorrentDisk, Bitfield>? verify = null)
    : ITorrentEngine, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Held> _torrents = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly byte[] _peerId = PeerIdentity.New();
    private readonly CancellationTokenSource _stopping = new();
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();

    /// <summary>
    /// The owner's rate limits, shared by every torrent: the line is what has a
    /// speed, not a torrent.
    /// </summary>
    private readonly RateLimits _limits = new(time ?? TimeProvider.System)
    {
        Download = { BytesPerSecond = maxDownloadRate },
        Upload = { BytesPerSecond = maxUploadRate },
    };
    private ListenSockets? _sockets;

    /// <summary>
    /// The one DHT this client has, shared by every torrent on it.
    /// </summary>
    /// <remarks>
    /// One routing table for the process, because it is the network's map and
    /// not any torrent's: a table per torrent would bootstrap from nothing
    /// every time and know a fraction of what one shared table knows.
    /// </remarks>
    private Dht? _dht;

    private SocketDhtTransport? _dhtSocket;

    /// <summary>
    /// The local network, which no tracker and no DHT knows about.
    /// </summary>
    /// <remarks>
    /// Worth little on a machine that is the only client on its network and
    /// nothing at all when it is not — but it costs one multicast packet every
    /// five minutes, and a second client on the same network is the one peer
    /// that is never behind anybody's router.
    /// </remarks>
    private LsdSocket? _local;

    /// <summary>What this client calls itself on the local network.</summary>
    /// <remarks>
    /// Its own announcements come back to it, and this is how they are told
    /// apart from somebody else's.
    /// </remarks>
    private readonly string _cookie = Convert.ToHexString(RandomNumberGenerator.GetBytes(8));
    private bool _started;
    private bool _disposed;

    /// <summary>The port it is really listening on, or null before it starts.</summary>
    public int? Port => _sockets?.Port;

    /// <summary>
    /// What became of the attempt to have the router open the port.
    /// </summary>
    /// <remarks>
    /// Null until the router has answered. <strong>Not drawn on any page and
    /// not consulted by <see cref="PortCondition"/>:</strong> a router that
    /// refuses says it would not open the port by itself, which on a
    /// hand-forwarded port means nothing. Kept for the log, where S12-06 put
    /// it, because "UPnP found no device" and "the gateway refused" are
    /// different problems when one is being chased.
    /// </remarks>
    public PortMapResult? Mapped { get; private set; }

    /// <summary>Whether any peer has ever arrived from outside this network.</summary>
    /// <remarks>
    /// Proof that the port is open, and the only proof there is: a mapping
    /// protocol refusing says the router would not open the port by itself and
    /// nothing about whether it is open. The Settings page stops asking for a
    /// forward once somebody has come through it.
    /// </remarks>
    public bool Reached { get; private set; }

    /// <summary>What is known about the listening port.</summary>
    /// <remarks>
    /// Derived from <see cref="Reached"/> and from nothing else. <see cref="Mapped"/>
    /// is deliberately not consulted: the router refusing to map a port says
    /// only that it would not do it by itself, and answering "shut" from that
    /// is the fault this replaced. <see cref="PortState.Shut"/> waits on a live
    /// check the host cannot yet make — media-server #52.
    /// </remarks>
    public PortState PortCondition => Reached ? PortState.Open : PortState.Unknown;

    /// <summary>Why it is not listening, when it is not.</summary>
    /// <remarks>
    /// Kept rather than thrown away: the Settings page says which port could
    /// not be bound and the owner changes it. A client that failed silently is
    /// one that looks like a network with no peers on it.
    /// </remarks>
    public string? Failure { get; private set; }

    /// <summary>
    /// Binds the port and makes the client ready, once.
    /// </summary>
    /// <remarks>
    /// A port that cannot be bound is reported and does not throw: a server
    /// behind a router that refuses the mapping still downloads from peers it
    /// dials out to, and taking the plugin down over it would cost the owner
    /// everything else it does.
    /// </remarks>
    public void Start()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_started)
            {
                return;
            }

            _started = true;

            try
            {
                _sockets = ListenSockets.Bind(listenPort);

                logger.LogInformation("The torrent client is listening on port {Port}.", _sockets.Port);

                // Somebody has to be at the door. A client that only dials out
                // never seeds to a peer that found it and never meets the half
                // of a swarm that is behind a router of its own.
                _ = AcceptingAsync(_sockets.Tcp, _stopping.Token);

                // Discarded on purpose: a router takes seconds to answer and
                // nothing may wait on it. The client is perfectly usable while
                // it is being asked, and the answer only changes what the
                // Settings page says.
                _ = MappingAsync(_sockets.Port, _stopping.Token);

                // The DHT, which is where the peers a tracker never names
                // are. Bootstrapped in the background for the same reason as
                // the router: it takes seconds of asking strangers, and every
                // torrent works without it in the meantime.
                NodeId me = NodeId.Random();

                _dhtSocket = new();
                _dht = new(me, new RoutingTable(me), _dhtSocket);

                _ = BootstrappingAsync(_dht, _stopping.Token);

                // And the network this machine is on, which neither a tracker
                // nor the DHT can see.
                _local = new();

                _ = AnnouncingLocallyAsync(_local, _sockets.Port, _stopping.Token);
                _ = ListeningLocallyAsync(_local, _stopping.Token);
            }
            catch (PortInUseException refused)
            {
                Failure = refused.Message;

                // Named with the number, and said once. The client carries on
                // without a listening socket: outgoing connections still work,
                // and half a client is worth more than none.
                logger.LogWarning("{Reason}", refused.Message);
                journal.Failed(ActivityStage.Download, $"port {refused.Port}", refused.Message);
            }
        }
    }

    /// <summary>
    /// Asks the router to open the listening port.
    /// </summary>
    /// <remarks>
    /// <c>PortMapping</c> from settings, UPnP first and then NAT-PMP. Both were
    /// written and tested in Sprint 6 and neither had a socket behind it, so
    /// the setting said the port would be opened and no port was ever opened —
    /// which is every peer in a public swarm refusing this client's connection
    /// because they are behind their own routers too.
    /// </remarks>
    private async Task MappingAsync(int port, CancellationToken ct)
    {
        if (mapping is null)
        {
            return;
        }

        try
        {
            PortMapResult result = await mapping.MapAsync(port, ct).ConfigureAwait(false);

            Mapped = result;

            if (result.Mapped)
            {
                logger.LogInformation("The router opened port {Port} over {How}.", result.Port, result.By);

                return;
            }

            // Not a failure of the client, and not a shut port either: it says
            // the router would not open it by itself. Written as "could not be
            // opened" it read as "your port is closed" to an owner who had
            // forwarded it by hand months earlier.
            logger.LogInformation(
                "Port {Port} could not be opened automatically; if it is forwarded on the router "
                + "there is nothing to do: {Reason}",
                port,
                result.Reason);
        }
        catch (Exception refused) when (refused is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Mapped = new(MappedBy.Nothing, port, refused.Message);

            logger.LogInformation(refused, "Port {Port} could not be opened automatically.", port);
        }
    }

    public async Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        if (Magnet.Parse(request.Source) is Magnet magnet)
        {
            return Take(magnet.InfoHash, magnet.DisplayName, magnet.Trackers, null, request);
        }

        // A .torrent, which the search chain never produces — every copy it
        // chooses carries a hash, and a hash is a magnet — but which the owner
        // hands over by name from a site that offers nothing else, and which is
        // how one instance of this client seeds to another.
        TorrentMetadata torrent = await TorrentAsync(request.Source, ct).ConfigureAwait(false);

        return Take(torrent.InfoHash, torrent.Name, torrent.Trackers, torrent, request);
    }

    /// <summary>
    /// Reads a <c>.torrent</c> off the disk or off an address.
    /// </summary>
    /// <remarks>
    /// Refused by name when it is neither. "The source is not supported" leaves
    /// the owner looking at a page with no idea which of the things they pasted
    /// was wrong.
    /// </remarks>
    private async Task<TorrentMetadata> TorrentAsync(string source, CancellationToken ct)
    {
        try
        {
            if (Uri.TryCreate(source, UriKind.Absolute, out Uri? address)
                && address.Scheme is "http" or "https")
            {
                return TorrentMetadata.Read(await transport.GetAsync(address, ct).ConfigureAwait(false));
            }

            if (File.Exists(source))
            {
                return TorrentMetadata.Read(await File.ReadAllBytesAsync(source, ct).ConfigureAwait(false));
            }
        }
        catch (Exception unreadable) when (unreadable is not OperationCanceledException)
        {
            throw new NotSupportedException($"'{source}' could not be read as a torrent: {unreadable.Message}");
        }

        throw new NotSupportedException($"'{source}' is neither a magnet nor a torrent this client can read.");
    }

    /// <summary>Takes on one torrent, however it was named.</summary>
    private TorrentHandle Take(
        string infoHash,
        string? name,
        IReadOnlyList<string> trackers,
        TorrentMetadata? torrent,
        TorrentRequest request)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_torrents.TryGetValue(infoHash, out Held? already))
            {
                // The same torrent from a second source is one torrent with
                // more trackers, which is the whole reason every indexer is
                // asked.
                already.Run.Add(request.Trackers.Union(trackers, StringComparer.OrdinalIgnoreCase));

                return new(infoHash, already.Run.Torrent?.Name ?? name);
            }

            // What was written down when it last had metadata. A torrent added
            // from a magnet otherwise asks the swarm for it again after every
            // restart, and a swarm that has gone quiet cannot answer — so a
            // torrent already complete on disk is given up on for want of a
            // file list nobody had to ask for.
            // Every tracker the run before this one had come to know. A magnet
            // off an indexer names none, and the trackers a torrent actually
            // announces to are learned after it is added — so without this a
            // restart leaves it with nobody to ask, announcing to nothing and
            // saying nothing about it, on the DHT alone. On 3 September 2026
            // that was Rings of Power S02E06: twenty-one of fifty-nine trackers
            // answering before the restart and not one announce in the thirty-six
            // minutes after it.
            IReadOnlyList<string> learned = resume?.Load(infoHash)?.Trackers ?? [];

            TorrentMetadata? known = torrent ?? Remembered(infoHash, [.. trackers.Union(learned, StringComparer.OrdinalIgnoreCase)]);

            TorrentRun run = new(
                Convert.FromHexString(infoHash),

                // Everything anybody named for it, without duplicates.
                [.. trackers.Union(request.Trackers, StringComparer.OrdinalIgnoreCase).Union(learned, StringComparer.OrdinalIgnoreCase)],
                request.DownloadFolder,
                new TrackerSet(transport, _time),
                dialler,
                _peerId,
                _sockets?.Port ?? listenPort,
                _time,
                known,
                resume,

                // The owner's rule, handed to an engine that has no idea what a
                // video file is: only the video files in a torrent are ever
                // downloaded, and samples are not among them.
                files =>
                [
                    .. Staging
                        .Wanted([.. files.Select(file => new TorrentFile(file.Path, file.Length))])
                        .Select(kept => files.First(file => string.Equals(file.Path, kept.Path, StringComparison.Ordinal))),
                ],
                _limits,

                // The client's own, shared by every torrent: the DHT is a map
                // of the network rather than of any one swarm.
                _dht,

                // The disk pass, replaceable so a test can hold one open. What
                // the client must go on answering through is that pass, and a
                // test cannot hold a real one open for long enough to tell.
                verify);

            // Before it is held, so a torrent that is whole on disk already —
            // which is every staged and dispatched grab after a restart — is
            // not announced to nobody. The run opens its session on its first
            // announcing pass, which the loop below starts.
            run.Finished += () => Whole(infoHash);
            run.Progressed += () => Moved(infoHash);
            run.Opened += () => Knows(infoHash);

            Held held = new(run, name, _time.GetUtcNow(), new(stallLimit, _time));

            _torrents[infoHash] = held;

            // What only an opened session can decide: whether there is a video
            // file in this torrent at all — a fake release has perfectly good
            // metadata — and whether the disk has room for the files that will
            // actually be fetched. Both used to be reached from Expire on every
            // tick, so a fake release was refused whenever something next asked
            // the client a question.

            // The stall clock starts when the torrent is taken on, not when
            // its first deadline comes round. StallWatch judges progress
            // between two readings, so without a reading here the first one
            // would be taken half an hour in and the second half an hour after
            // that - an hour to notice a torrent that never had a peer.
            RunProgress opening = held.Run.Progress();

            held.Stall.Observe(opening.BytesDone, opening.Peers);

            // A new torrent may be one too many. This ran on every status call
            // and nowhere else, so the concurrency limit was whatever the last
            // page to be drawn had left it at.
            Queue();

            // And the deadline it is owed from this moment: the owner's
            // metadata limit for a magnet, the stall limit for a torrent that
            // knows what it is.
            Rearm(infoHash, held);

            // Discarded on purpose: the loop stops on the token and cannot
            // fault, because everything inside it is caught. Holding the task
            // would suggest something waits for it, and nothing does — a
            // shutdown that waited on an announce would wait on a socket.
            _ = AnnouncingAsync(held, _stopping.Token);

            return new(infoHash, name);
        }
    }

    /// <summary>
    /// Every tracker known for one torrent.
    /// </summary>
    /// <remarks>
    /// Not on the port: the pipeline has no business with them, and what
    /// announces to them is this client. It is here because the same torrent
    /// arrives from several sites and each brings its own — more trackers is a
    /// faster download, and that is the whole reason every indexer is asked.
    /// </remarks>
    public IReadOnlyList<string> TrackersOf(string infoHash)
    {
        lock (_lock)
        {
            return _torrents.TryGetValue(infoHash, out Held? held) ? held.Run.Trackers : [];
        }
    }

    /// <summary>What this client is holding, and nothing else.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to be the client's entire housekeeping.</strong> It ran
    /// <see cref="Expire"/>, <see cref="Stalled"/>, <see cref="Seeded"/>,
    /// <see cref="Queue"/> and the resume write on every call, and nothing else
    /// called any of them — so drawing a page did the client's work, and not
    /// drawing one meant none was done. A magnet past its limit failed on
    /// whichever page happened to be opened next, and a finished download was
    /// found by whatever asked first.
    /// </para>
    /// <para>
    /// That is the real reason the transfers cadence was <c>* * * * *</c>: not
    /// transfers, but that without a tick a minute this client stopped doing
    /// any of it. Each of the five now runs when its own moment comes — see
    /// <see cref="Rearm"/> for the three that are deadlines and the events for
    /// the rest — and asking what the client holds changes nothing about what
    /// it holds.
    /// </para>
    /// </remarks>
    public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<TorrentStatus>>([.. _torrents.Select(one => Status(one.Key, one.Value))]);
        }
    }

    /// <summary>
    /// Sets this torrent's one deadline, or takes it away where none is owed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Three things in this client cannot be told by an event, because
    /// they are about something not happening.</strong> Nobody sends a message
    /// saying they will not serve a magnet's metadata, and nothing announces
    /// that no byte has arrived for twenty minutes. The owner's
    /// <c>MetadataTimeoutMinutes</c> and <c>StallMinutes</c> are durations, and
    /// a duration can only be measured by asking over and over — which is the
    /// poll this work exists to remove — or by waking once at the end of it.
    /// This wakes once at the end of it.
    /// </para>
    /// <para>
    /// <strong>On a download that is running it never goes off.</strong>
    /// <see cref="Moved"/> cancels and sets it again every time a piece really
    /// verifies, so it can only reach its end when the torrent has genuinely
    /// stopped. A client holding nothing has no timer at all.
    /// </para>
    /// <para>
    /// Why this client gives up when no ordinary one does: there is nobody
    /// watching it. qBittorrent leaves a magnet on "fetching metadata" for ever
    /// and calls a dead torrent "stalled" in a list, because a person decides
    /// what to do about it. Here the failure is what frees the episode to be
    /// searched for again, and without it the episode is never looked for.
    /// </para>
    /// </remarks>
    private void Rearm(string infoHash, Held held)
    {
        held.Wake?.Dispose();
        held.Wake = null;

        DateTimeOffset now = _time.GetUtcNow();

        if (Owed(held, now) is not TimeSpan wait)
        {
            return;
        }

        held.Armed = now;
        held.Wake = _time.CreateTimer(_ => Woke(infoHash), null, wait, Timeout.InfiniteTimeSpan);
    }

    /// <summary>How long until this torrent owes an answer, or null where it owes none.</summary>
    private TimeSpan? Owed(Held held, DateTimeOffset now)
    {
        // Already given up on, or stopped by the owner. Neither is waiting for
        // anything, and a paused torrent must never be failed for having sat
        // there while it was stopped.
        if (held.Error is not null || held.Run.Paused)
        {
            return null;
        }

        if (held.Finished is not null)
        {
            // Seeding, and nothing else is owed: it cannot stall, and it knows
            // what it is. A public torrent is stopped the moment it completes
            // and never reaches here; a private one with no hours set seeds
            // until its ratio is met, which the session says without being
            // asked.
            return seeding.For > TimeSpan.Zero
                ? From(held.Finished.Value + seeding.For - now)
                : null;
        }

        // The earliest of what is owed, never the first of them. A magnet is
        // waiting on two at once — somebody to serve its metadata, and anything
        // at all to happen — and taking only the metadata limit left a magnet
        // with no peer at all sitting there until that limit passed, however
        // much sooner the owner's stall limit came round.
        DateTimeOffset stall = (held.Stall.StuckSince ?? now) + stallLimit;

        DateTimeOffset first = held.Run.Torrent is null
            ? Earlier(held.Since + metadataTimeout, stall)
            : stall;

        return From(first - now);
    }

    /// <summary>Whichever of two moments comes first.</summary>
    private static DateTimeOffset Earlier(DateTimeOffset one, DateTimeOffset other) =>
        one < other ? one : other;

    /// <summary>A wait that is never negative, because a deadline already past is due now.</summary>
    private static TimeSpan From(TimeSpan wait) => wait > TimeSpan.Zero ? wait : TimeSpan.Zero;

    /// <summary>The deadline came, so the answer it was waiting on is given.</summary>
    private void Woke(string infoHash)
    {
        bool gaveUp;

        lock (_lock)
        {
            if (!_torrents.TryGetValue(infoHash, out Held? held))
            {
                // Removed while its timer was running. Nothing to decide.
                return;
            }

            DateTimeOffset now = _time.GetUtcNow();
            bool failing = held.Error is not null;

            Expire(held, now);
            Stalled(held);
            Seeded(held, now);
            Queue();
            Rearm(infoHash, held);

            gaveUp = !failing && held.Error is not null;
        }

        if (gaveUp)
        {
            GiveUp(infoHash);
        }

        Stir();
    }

    /// <summary>A piece of this torrent verified, so it is alive.</summary>
    /// <remarks>
    /// <para>
    /// The stall deadline is pushed back rather than reached, which is why a
    /// running download never has a timer go off. Pushed back at most four
    /// times in a stall limit, because a torrent taking ten megabytes a second
    /// verifies several pieces a second and remaking a timer that often would
    /// cost more than the deadline saves.
    /// </para>
    /// <para>
    /// And the resume files, which were written by whoever happened to ask this
    /// client for its status. <c>ResumeKeeper</c> keeps the owner's own
    /// interval and answers at once when it has not passed, so there is nothing
    /// to write when nothing has arrived — which is exactly when a resume file
    /// has nothing new to say.
    /// </para>
    /// </remarks>
    private void Moved(string infoHash)
    {
        bool stirred;

        lock (_lock)
        {
            if (!_torrents.TryGetValue(infoHash, out Held? held))
            {
                return;
            }

            stirred = _time.GetUtcNow() - held.Armed >= stallLimit / 4;

            if (stirred)
            {
                Rearm(infoHash, held);
            }

            Remember();
        }

        if (stirred)
        {
            Stir();
        }
    }

    /// <summary>Writes the resume files, if the owner's interval has passed.</summary>
    /// <remarks>
    /// <para>
    /// Called where something worth writing down has happened and nowhere else:
    /// a piece verified, a run settled what it is holding, a torrent finished,
    /// the owner stopped one. <c>ResumeKeeper</c> keeps its own interval and
    /// answers at once when it has not passed, so this costs a comparison on
    /// the ones that are too soon.
    /// </para>
    /// <para>
    /// <strong>A verified piece is not enough on its own.</strong> A torrent
    /// that is already whole on disk — which is every staged and dispatched
    /// grab after a restart, and every download the moment it finishes — never
    /// verifies another piece as long as it lives, so a resume file hung on
    /// progress alone would never be written for exactly the torrents whose
    /// resume file matters most. Settling and finishing are where the verified
    /// bitfield becomes worth keeping, and they are where this is called.
    /// </para>
    /// </remarks>
    private void Remember()
    {
        resume?.Tick(_torrents.Values.Select(one => one.Run.Resuming()).OfType<ResumeData>());
    }

    /// <summary>The metadata arrived, so what only it can decide is decided.</summary>
    /// <remarks>
    /// <see cref="Refuse"/> and <see cref="Cramped"/> both need the file list
    /// and the size, and both used to be reached from <see cref="Expire"/> on
    /// every tick — so a fake release was found, and a torrent too big for the
    /// disk was stopped, only when something happened to ask. This is the
    /// moment they can be answered, and it is the moment they are.
    /// </remarks>
    private void Knows(string infoHash)
    {
        bool gaveUp;

        lock (_lock)
        {
            // Already given up on: refusing it a second time would write a
            // second line into the journal for one torrent, which is the fault
            // TheFailureIsSaidOnceAndNotOnceATick exists to stop.
            if (!_torrents.TryGetValue(infoHash, out Held? held) || held.Error is not null)
            {
                return;
            }

            Refuse(held);
            Cramped(held);
            Queue();
            Rearm(infoHash, held);

            // What the disk pass just found, written down. This is the first
            // moment the verified bitfield exists, and for a torrent already
            // whole on disk it is the only one.
            Remember();

            gaveUp = held.Error is not null;
        }

        if (gaveUp)
        {
            GiveUp(infoHash);
        }

        Stir();
    }

    /// <summary>This torrent is whole, so the seeding policy is applied to it.</summary>
    private void Whole(string infoHash)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Held? held))
            {
                Seeded(held, _time.GetUtcNow());

                // A slot has come free: a seeding torrent is not downloading,
                // and the next in the queue has been waiting for this.
                Queue();

                GiveBack(infoHash, held);
                Rearm(infoHash, held);

                // Whole, and worth writing down before anything can go wrong.
                Remember();
            }
        }

        // Outside the lock. A handler stages a file and asks the server for an
        // encode, and neither has any business waiting on this client's lock.
        Finished(infoHash);
        Stir();
    }

    /// <summary>
    /// Asks the session to say when the owner's ratio has been given back.
    /// </summary>
    /// <remarks>
    /// A ratio is a count of bytes once the download has stopped moving, so the
    /// session can say when they have gone out instead of this client asking
    /// what the ratio is. Only a private torrent ever gives anything back.
    /// </remarks>
    private void GiveBack(string infoHash, Held held)
    {
        if (seeding.Ratio <= 0 || held.Run.Torrent is not { Private: true })
        {
            return;
        }

        RunProgress progress = held.Run.Progress();

        if (progress.Downloaded <= 0)
        {
            return;
        }

        held.Run.TellMeWhenGivenBack((long)(progress.Downloaded * seeding.Ratio), () => Repaid(infoHash));
    }

    /// <summary>The owner's ratio has been met, so this stops seeding.</summary>
    private void Repaid(string infoHash)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Held? held))
            {
                Seeded(held, _time.GetUtcNow());
                Rearm(infoHash, held);
            }
        }
    }

    public Task PauseAsync(string infoHash, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Held? held))
            {
                // The verified pieces and the disk stay exactly as they are:
                // that is what makes resuming cost nothing. What goes is the
                // conversations, because a paused torrent still answering peers
                // is not paused.
                held.Run.Pause();

                // The owner's decision, not the queue's. Left marked as queued
                // it would be started again the moment a slot came free.
                held.Queued = false;

                // A paused torrent owes no answer: it is not failing to fetch
                // its metadata and it is not stalled, it is stopped. Its
                // deadline goes with it, and a slot has come free.
                Queue();
                Rearm(infoHash, held);

                // Stopping is a good moment to write down where it got to.
                Remember();
            }

            return Task.CompletedTask;
        }
    }

    public Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Held? held))
            {
                held.Run.Resume();
                held.Queued = false;

                // The clock starts again with it. A torrent resumed after the
                // limit had passed would otherwise fail the moment it started,
                // without a single peer having been asked.
                held.Since = _time.GetUtcNow();
                held.Error = null;

                Queue();
                Rearm(infoHash, held);
            }

            return Task.CompletedTask;
        }
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        Held? held = Taken(infoHash);

        if (held is null)
        {
            // Not held, and its files are still on the owner's disk. This used
            // to return here, so a removal asked for after a restart — before
            // the plugin had handed the torrent back — deleted nothing and said
            // nothing, while the caller went on to mark the grab done. On
            // 5 September 2026 that was 9.4 GB in the owner's download folder
            // that no grab answered for.
            //
            // Nameable without holding it, because the metadata is kept beside
            // the download for exactly this: the resume keeper's folder is the
            // download folder, and the info dictionary says which files under
            // it are this torrent's.
            if (deleteFiles && resume is not null)
            {
                Delete(resume.Folder, Remembered(infoHash, []), infoHash);
            }

            resume?.Forget(infoHash);

            return Task.CompletedTask;
        }

        // What it wrote is its file list, and that is read before the resume
        // keeper forgets it: the copy kept beside the download is what a run
        // that was re-added from a magnet knows its own files by.
        TorrentMetadata? torrent = held.Run.Torrent ?? Remembered(infoHash, []);

        // Outside the lock: disposing a run waits on nothing, but the files it
        // may be asked to delete are a disk operation and the rest of the client
        // must not stop for it.
        held.Run.Dispose();

        if (deleteFiles)
        {
            Delete(held.Run.Folder(), torrent, infoHash);
        }

        resume?.Forget(infoHash);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Clears every download no grab answers for any more.
    /// </summary>
    /// <param name="keep">The hashes the store still has a grab for.</param>
    /// <returns>What was cleared, so the caller can say so.</returns>
    /// <remarks>
    /// <para>
    /// A download whose grab has gone — cancelled, or pruned — leaves its
    /// folder, its metadata and its resume file behind, and nothing ever asks
    /// for them again. On 5 September 2026 that was 8.6 GB of a season pack
    /// cancelled days earlier, in a folder the owner had to clear by hand. The
    /// rule since 24 August is that a download that is over leaves nothing.
    /// </para>
    /// <para>
    /// <strong>Only what this plugin wrote itself.</strong> A torrent is
    /// recognised by the metadata kept beside its download, and what is deleted
    /// is what that metadata names. A folder the owner put in there is not a
    /// torrent, is recognised as nothing, and is left exactly where it is —
    /// which is why this reads the <c>.info</c> files rather than the folder
    /// listing.
    /// </para>
    /// <para>
    /// Anything the client is holding is kept whatever the store says: a
    /// torrent between being added and being written down is held and not yet
    /// grabbed, and deleting it from under itself would be this sweep causing
    /// the fault it exists to clear.
    /// </para>
    /// </remarks>
    public IReadOnlyList<string> ForgetAbandoned(IReadOnlyCollection<string> keep)
    {
        if (resume is null || !Directory.Exists(resume.Folder))
        {
            return [];
        }

        HashSet<string> answered = new(keep, StringComparer.OrdinalIgnoreCase);
        List<string> cleared = [];

        foreach (string path in Directory.EnumerateFiles(resume.Folder, "*.info"))
        {
            string infoHash = Path.GetFileNameWithoutExtension(path);

            if (answered.Contains(infoHash))
            {
                continue;
            }

            lock (_lock)
            {
                if (_torrents.ContainsKey(infoHash))
                {
                    continue;
                }
            }

            Delete(resume.Folder, Remembered(infoHash, []), infoHash);
            resume.Forget(infoHash);
            cleared.Add(infoHash);
        }

        return cleared;
    }

    public Task ReleaseAsync(string infoHash, CancellationToken ct)
    {
        if (Taken(infoHash) is not Held held)
        {
            return Task.CompletedTask;
        }

        // What it had verified, written before the run goes: a disposed run has
        // nothing left to say, and this is what lets it be added back without
        // every piece being read again. Its metadata stays where it was written
        // when it was fetched, which is what RemoveAsync forgets and this does
        // not — so a torrent let go of comes back without asking its swarm, and
        // its files can still be named and deleted when its grab is done.
        if (held.Run.Resuming() is ResumeData verified)
        {
            resume?.Stop([verified]);
        }

        // Closes every file it had open, which is the point: Windows moves no
        // file that is open.
        held.Run.Dispose();

        return Task.CompletedTask;
    }

    /// <summary>Takes a torrent out of the table, and answers what it was holding.</summary>
    private Held? Taken(string infoHash)
    {
        Held? held;

        lock (_lock)
        {
            _torrents.Remove(infoHash, out held);

            // Its deadline goes with it. A timer left behind would wake for a
            // torrent this client no longer holds, which Woke would find
            // nothing for — and holding a disposed run's timer alive is a
            // leak per removed torrent.
            held?.Wake?.Dispose();

            if (held is not null)
            {
                // A slot has come free for whatever the concurrency limit was
                // keeping back.
                Queue();
            }
        }

        return held;
    }

    public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
    {
        lock (_lock)
        {
            // Empty while the metadata has not arrived, never a guess from the
            // name: inventing a file list is how the wrong file gets staged.
            return Task.FromResult<IReadOnlyList<TorrentFile>>(
                _torrents.TryGetValue(infoHash, out Held? held) && held.Run.Torrent is TorrentMetadata torrent
                    ? [.. torrent.Files.Select(file => new TorrentFile(torrent.PathUnderFolder(file), file.Length))]
                    : []);
        }
    }

    public void Dispose()
    {
        List<Held> holding;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            holding = [.. _torrents.Values];

            // Every deadline goes here, and under the lock: a timer that fires
            // during a shutdown finds Woke looking in a dictionary that is
            // being emptied.
            foreach (Held one in holding)
            {
                one.Wake?.Dispose();
                one.Wake = null;
            }

            _torrents.Clear();
        }

        // The loops stop before the runs go, or one of them announces to a
        // tracker on behalf of a torrent that has been disposed.
        _stopping.Cancel();

        // What every torrent had verified, and before the runs go: a disposed
        // run has nothing left to say. A clean stop is the one moment this can
        // be written with no piece of it in flight, and the whole point of it
        // is that the next start believes it instead of downloading it again.
        resume?.Stop(holding.Select(one => one.Run.Resuming()).OfType<ResumeData>());

        foreach (Held held in holding)
        {
            held.Run.Dispose();
        }

        if (mapping is not null && Mapped?.Mapped == true)
        {
            // Taken away on the way out, and waited for: a mapping left behind
            // sits in the router's list pointing at a machine that is no longer
            // listening, and the owner finds it there months later.
            //
            // Its own token, because the client's has just been cancelled.
            using CancellationTokenSource leaving = new(TimeSpan.FromSeconds(10));

            try
            {
                mapping.UnmapAsync(Mapped.Port, leaving.Token).GetAwaiter().GetResult();
            }
            catch (Exception refused) when (refused is not OperationCanceledException)
            {
                logger.LogWarning(refused, "The port mapping could not be taken away.");
            }
            catch (OperationCanceledException)
            {
                logger.LogWarning("The router did not answer in time to take the port mapping away.");
            }
        }

        _stopping.Dispose();

        lock (_lock)
        {
            _sockets?.Dispose();

            // The DHT's socket with them. Its own reading loop stops on the
            // token, and a socket left open would hold the port after the
            // plugin has gone.
            _local?.Dispose();
            _local = null;
            _dhtSocket?.Dispose();
            _dhtSocket = null;
            _dht = null;
            _sockets = null;
        }
    }

    /// <summary>
    /// Announces for one torrent, at the interval its trackers asked for.
    /// </summary>
    /// <remarks>
    /// docs/06-torrent-client.md: announce at the tracker's own interval. It
    /// runs for the client's life rather than once, because a swarm changes and a
    /// client that only asked once would be left with the peers of five minutes
    /// ago.
    /// </remarks>
    private async Task AnnouncingAsync(Held held, CancellationToken ct)
    {
        // Off the caller's thread before anything else. This is started from
        // inside the client's lock, and the first thing an announce pass does
        // is open the session, which reads and hashes every byte already on
        // disk: without this the hashing ran on the thread that added the
        // torrent, under the lock, and every page and every tick waited on it
        // for as long as a season pack takes to read.
        await Task.Yield();

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await held.Run.OnceAsync(ct).ConfigureAwait(false);

                Said(held);
            }
            catch (Exception wrong) when (wrong is not OperationCanceledException)
            {
                // One torrent is one torrent. A tracker set that threw must not
                // stop every other torrent from announcing.
                logger.LogWarning("Announcing failed: {Reason}", wrong.Message);
            }

            try
            {
                // What the run asks for rather than the tracker's interval,
                // which is not the same number when the run has lost everybody:
                // it announces at the tracker's interval either way, and dials
                // oftener while it has nobody to dial anyone from.
                await Task.Delay(held.Run.Wait, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// Finds the DHT's own network, once, at startup.
    /// </summary>
    /// <remarks>
    /// A routing table that knows nobody can find nobody, and the only way in
    /// is somebody already there — the two addresses in
    /// <see cref="Dht.BootstrapNodes"/>, resolved because they move. A failure
    /// costs the client its DHT and nothing else: the trackers and the peer
    /// exchanges carry on exactly as they were.
    /// </remarks>
    private async Task BootstrappingAsync(Dht dht, CancellationToken ct)
    {
        List<IPEndPoint> nodes = [];

        foreach (string address in Dht.BootstrapNodes)
        {
            string[] parts = address.Split(':');

            if (parts.Length != 2 || !int.TryParse(parts[1], out int port))
            {
                continue;
            }

            try
            {
                foreach (IPAddress found in await Dns.GetHostAddressesAsync(parts[0], ct).ConfigureAwait(false))
                {
                    if (found.AddressFamily == AddressFamily.InterNetwork)
                    {
                        nodes.Add(new(found, port));
                    }
                }
            }
            catch (Exception unreachable) when (unreachable is not OperationCanceledException)
            {
                logger.LogDebug("{Node} could not be resolved: {Why}", address, unreachable.Message);
            }
        }

        if (nodes.Count == 0)
        {
            logger.LogWarning("No DHT bootstrap node could be resolved, so peers come from trackers alone.");

            return;
        }

        try
        {
            await dht.BootstrapAsync(nodes, ct).ConfigureAwait(false);

            logger.LogInformation("The DHT bootstrap answered with {Count} nodes.", dht.Table.Count);
        }
        catch (Exception quiet) when (quiet is not OperationCanceledException)
        {
            logger.LogWarning("The DHT could not be joined: {Why}", quiet.Message);

            return;
        }

        // And then the join itself, again and again. The bootstrap is two
        // routers and the handful of contacts they name; the walk towards our
        // own id is what fills the buckets around us, and it is repeated
        // because nodes go and a table that was right at boot is not a table.
        while (!ct.IsCancellationRequested)
        {
            try
            {
                logger.LogInformation("The DHT knows {Count} nodes.", await dht.JoinAsync(ct).ConfigureAwait(false));
            }
            catch (Exception quiet) when (quiet is not OperationCanceledException)
            {
                logger.LogDebug("The DHT walk stopped: {Why}", quiet.Message);
            }

            try
            {
                await Task.Delay(DhtRefresh, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>How often the table is walked again.</summary>
    /// <remarks>
    /// Fifteen minutes is what BEP 5 asks of a bucket that has gone quiet, and
    /// a walk costs eight questions a round against nodes that are answering
    /// anyway.
    /// </remarks>
    private static readonly TimeSpan DhtRefresh = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Says on the local network what this client is holding.
    /// </summary>
    /// <remarks>
    /// Private torrents are never among them: the packet carries the info hash
    /// in the clear to everybody on the network, which is precisely what a
    /// private tracker's members are forbidden to do.
    /// </remarks>
    private async Task AnnouncingLocallyAsync(LsdSocket socket, int port, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                string[] holding;

                lock (_lock)
                {
                    holding = [.. LocalDiscovery.Announceable(
                        _torrents.Values.Select(one => one.Run.Torrent).OfType<TorrentMetadata>())];
                }

                if (holding.Length > 0)
                {
                    await socket.AnnounceAsync(port, holding, _cookie, ct).ConfigureAwait(false);
                }
            }
            catch (Exception quiet) when (quiet is not OperationCanceledException)
            {
                // A network that will not take a multicast packet is the
                // ordinary case on plenty of machines, and costs this client
                // nothing but the peers it would have found on it.
                logger.LogDebug("Local discovery could not announce: {Why}", quiet.Message);
            }

            try
            {
                await Task.Delay(LocalDiscovery.Interval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Dials whoever answers on the local network.</summary>
    private async Task ListeningLocallyAsync(LsdSocket socket, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                (LsdAnnounce announce, IPAddress from) = await socket.ReceiveAsync(_cookie, ct).ConfigureAwait(false);

                foreach (string hash in announce.InfoHashes)
                {
                    Held? held;

                    lock (_lock)
                    {
                        _torrents.TryGetValue(hash, out held);
                    }

                    // Only what this client is actually holding. Somebody else's
                    // torrent is not this client's business.
                    held?.Run.Met([new(from, announce.Port)]);
                }
            }
            catch (Exception gone)
            {
                if (gone is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                logger.LogDebug("Local discovery could not listen: {Why}", gone.Message);
            }
        }
    }

    /// <summary>
    /// Answers everybody who dials in, for as long as the client is up.
    /// </summary>
    /// <remarks>
    /// Each arrival is welcomed on its own and the loop goes straight back to
    /// the door. A client that introduced one peer before accepting the next
    /// would be held up by every peer that dialled and then said nothing, which
    /// is a great many of them.
    /// </remarks>
    private async Task AcceptingAsync(Socket listening, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket arrived;

            try
            {
                arrived = await listening.AcceptAsync(ct).ConfigureAwait(false);
            }
            catch (Exception closed) when (closed is not OperationCanceledException)
            {
                // The socket has gone, which is a client shutting down. There
                // is nothing left to accept on.
                return;
            }

            // A peer got through the door — but only one that crossed the
            // router proves anything. This client announces itself on the local
            // network, so a neighbour found by local service discovery reaches
            // this socket without the forwarded port being involved at all, and
            // drawing "open" from that would be a page confidently wrong about
            // the one thing the owner would act on.
            if (arrived.RemoteEndPoint is IPEndPoint from && DialIn.ProvesThePortIsOpen(from.Address))
            {
                Reached = true;
            }

            _ = WelcomeAsync(arrived, ct);
        }
    }

    /// <summary>Introduces one arrival and hands it to the torrent it came for.</summary>
    /// <remarks>
    /// A peer asking for a torrent this client is not holding is dropped, and
    /// so is one that hung up mid-handshake. Neither is a fault: a listening
    /// socket meets both every day.
    /// </remarks>
    private async Task WelcomeAsync(Socket arrived, CancellationToken ct)
    {
        NetworkStream wire = new(arrived, ownsSocket: true);

        try
        {
            PeerArrival? arrival = await PeerWelcome
                .AcceptAsync(wire, Holding(), _peerId, _random, ct)
                .ConfigureAwait(false);

            if (arrival is null)
            {
                await wire.DisposeAsync().ConfigureAwait(false);

                return;
            }

            string hash = Convert.ToHexString(arrival.InfoHash);
            Held? held;

            lock (_lock)
            {
                _torrents.TryGetValue(hash, out held);
            }

            if (held is null)
            {
                // Removed between the handshake and here, which is a race a
                // listening socket really runs.
                await wire.DisposeAsync().ConfigureAwait(false);

                return;
            }

            held.Run.Take(
                new PeerConnection(arrival.Wire, arrival.Introduction, held.Run.Torrent?.PieceCount ?? 0),
                ct);
        }
        catch (Exception gone) when (gone is not OperationCanceledException)
        {
            await wire.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Every info hash this client is holding, as the welcome wants them.</summary>
    private IReadOnlyCollection<byte[]> Holding()
    {
        lock (_lock)
        {
            return [.. _torrents.Keys.Select(Convert.FromHexString)];
        }
    }

    /// <summary>
    /// Fails a magnet whose metadata nobody in the swarm will serve.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>MetadataTimeoutMinutes</c> from settings. Without it a magnet with no
    /// peer that has the metadata sits in the list saying "fetching metadata"
    /// for as long as the server runs, and the episode it was grabbed for is
    /// never looked for again — which is what 0.3.4 did.
    /// </para>
    /// <para>
    /// Said once: it runs when the torrent's deadline comes, and the error it
    /// records is what takes the deadline away. A paused torrent owes no
    /// deadline, so it is never failed for having sat there while it was stopped.
    /// </para>
    /// </remarks>
    private void Expire(Held held, DateTimeOffset now)
    {
        if (held.Error is not null || held.Run.Paused)
        {
            return;
        }

        if (held.Run.Torrent is not null)
        {
            Refuse(held);
            Cramped(held);

            return;
        }

        if (now - held.Since < metadataTimeout)
        {
            return;
        }

        // Its own words, and they name the limit so the owner knows which
        // setting to change.
        held.Error = $"No peer sent its metadata within {metadataTimeout.TotalMinutes:0.#} minutes.";

        held.Run.Pause();

        logger.LogWarning("{Hash} was dropped: {Reason}", held.Run.Torrent?.Name ?? held.Name, held.Error);
        journal.Failed(ActivityStage.Download, held.Name ?? "a magnet", held.Error);
    }

    /// <summary>Everything the pages draw about the client, as one value.</summary>
    /// <remarks>
    /// <para>
    /// To be compared with the last one pushed. Where they differ something a
    /// page shows has changed and the page has to be told; where they are the
    /// same nothing has, and a push would be a poll written at the other end.
    /// </para>
    /// <para>
    /// Every figure the Downloads page draws is in here, and only those: a
    /// change nobody can see is not a change. So a peer arriving moves it, a
    /// seed arriving moves it, a peer choking us moves it, a byte moves it —
    /// and a torrent sitting still with the same peers does not.
    /// </para>
    /// </remarks>
    public string Drawn
    {
        get
        {
            System.Text.StringBuilder said = new();

            lock (_lock)
            {
                foreach ((string infoHash, Held held) in _torrents)
                {
                    RunProgress progress = held.Run.Progress();

                    said.Append(infoHash).Append(':')
                        .Append(State(held, progress)).Append(':')
                        .Append(progress.BytesDone).Append(':')
                        .Append(progress.BytesTotal).Append(':')
                        .Append(progress.Downloaded).Append(':')
                        .Append(progress.Uploaded).Append(':')
                        .Append((long)progress.DownloadRateBytesPerSecond).Append(':')
                        .Append((long)progress.UploadRateBytesPerSecond).Append(':')
                        .Append(progress.Seeds).Append(':')
                        .Append(progress.Leechers).Append(':')
                        .Append(progress.ChokedBy).Append(':')
                        .Append(progress.Askable).Append(':')
                        .Append(held.Run.SwarmSeeds).Append(':')
                        .Append(held.Run.SwarmPeers).Append(':')
                        .Append(held.Error)
                        .Append('|');
                }
            }

            return said.ToString();
        }
    }

    /// <summary>Says which torrent has finished, once per torrent.</summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what replaced the sweep.</strong> A completion was
    /// noticed by <see cref="Seeded"/>, which is reached from
    /// <see cref="StatusAsync"/> and from nowhere else — so nothing was staged
    /// and no encode was asked for until something happened to ask, and the
    /// transfers cadence was <c>* * * * *</c> to make that happen often enough
    /// for the owner not to see it.
    /// </para>
    /// <para>
    /// The info hash, because that is what the run is held under and what every
    /// grab in the store is keyed by. The handler is a plugin's, doing real
    /// work — staging a file, asking the server for an encode — so it is raised
    /// outside this client's lock, which is where the session raises it too.
    /// </para>
    /// </remarks>
    public event Action<string>? Completed;

    /// <summary>Says which torrent the client has given up on, once per failure.</summary>
    /// <remarks>
    /// <para>
    /// A deadline passing or a refusal on opening stops the torrent, and that is
    /// half of it: the grab is failed, the release blacklisted and the torrent
    /// taken out only by a transfers pass, and until then the client goes on
    /// holding it — which holds the cycle open. A pass ran every minute until the
    /// cycle became events, and then nothing started one. On 14 September 2026 an
    /// American Dad pack was dropped for its metadata at 17:23 and its grab still
    /// read "grabbed" forty minutes later.
    /// </para>
    /// <para>
    /// Raised outside this client's lock, for the same reason as
    /// <see cref="Completed"/>: the handler runs a pass that asks this client for
    /// its status and removes torrents from it.
    /// </para>
    /// </remarks>
    public event Action<string>? GaveUp;

    /// <summary>Raises <see cref="GaveUp"/> without letting a handler take the client down.</summary>
    private void GiveUp(string infoHash)
    {
        try
        {
            GaveUp?.Invoke(infoHash);
        }
        catch (Exception wrong)
        {
            logger.LogWarning(
                wrong, "{Hash} was given up on and something went wrong acting on it: {Reason}", infoHash, wrong.Message);
        }
    }

    /// <summary>Raised when the client does something a page would show.</summary>
    /// <remarks>
    /// <para>
    /// Settling what a torrent holds, a torrent finishing, the owner pausing or
    /// resuming one, a deadline deciding something, and pieces arriving — the
    /// last of those at most a few times a stall limit, on the same debounce as
    /// the stall deadline, because a torrent at ten megabytes a second verifies
    /// several pieces a second.
    /// </para>
    /// <para>
    /// What it is for: a page that has been open with nothing changing goes to
    /// rest, and nothing samples the client for it. A stalled torrent starting
    /// again is exactly the one somebody was staring at, and this is what wakes
    /// the watch. `S11-29`.
    /// </para>
    /// </remarks>
    public event Action? Stirred;

    /// <summary>Raises <see cref="Stirred"/>, and never lets a handler take the client down.</summary>
    private void Stir()
    {
        try
        {
            Stirred?.Invoke();
        }
        catch (Exception wrong)
        {
            logger.LogWarning(wrong, "Telling the pages the client moved went wrong: {Reason}", wrong.Message);
        }
    }

    /// <summary>Raises <see cref="Completed"/> without letting a handler take the client down.</summary>
    /// <remarks>
    /// This is reached from a peer's own loop, which has no caller to throw to:
    /// an escaping exception here would be an unobserved task exception and
    /// would take the media server with it rather than the download.
    /// </remarks>
    private void Finished(string infoHash)
    {
        try
        {
            Completed?.Invoke(infoHash);
        }
        catch (Exception wrong)
        {
            logger.LogWarning(
                wrong, "{Hash} finished and something went wrong acting on it: {Reason}", infoHash, wrong.Message);
        }
    }

    /// <summary>Whether there is any torrent at all to draw.</summary>
    /// <remarks>
    /// <para>
    /// The page's heartbeat used to push only while <see cref="Moving"/> — while
    /// some torrent was taking or giving bytes. So a download that stalled
    /// stopped updating the moment it stalled, and the owner had to refresh by
    /// hand to see anything.
    /// </para>
    /// <para>
    /// That is backwards. A stalled torrent is the one being watched, and the
    /// numbers that move while it stands still — peers, seeds, how many are
    /// choking us, how many hold anything wanted — are exactly the ones that
    /// say what is happening to it. Nought bytes a second is news.
    /// </para>
    /// </remarks>
    public bool Watching
    {
        get
        {
            lock (_lock)
            {
                return _torrents.Count > 0;
            }
        }
    }

    /// <summary>Whether any torrent is taking or giving bytes right now.</summary>
    /// <remarks>
    /// Read once a second by the page's heartbeat, so it holds the lock for as
    /// long as it takes to look at a rate and no longer. Asked rather than
    /// pushed because a torrent's byte count moves continuously and nothing in
    /// the journal marks it: without this the Downloads page drew the progress
    /// of the moment it was opened and did not move again until the owner
    /// refreshed it by hand.
    /// </remarks>
    public bool Moving
    {
        get
        {
            lock (_lock)
            {
                foreach (Held held in _torrents.Values)
                {
                    RunProgress progress = held.Run.Progress();

                    if (progress.DownloadRateBytesPerSecond > 0 || progress.UploadRateBytesPerSecond > 0)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }

    /// <summary>
    /// Writes down what an announce got back, once per announce.
    /// </summary>
    /// <remarks>
    /// The log said nothing about the swarm at all: a torrent taking ten
    /// megabytes a second and one sitting at nought wrote the same nothing, so
    /// on 31 August 2026 the only way to find out why the owner's pack had one
    /// peer in front of three hundred seeders was to run a second copy of the
    /// engine beside the server and measure it. One line an announce, naming
    /// the trackers that would not answer, is what makes that readable instead.
    /// </remarks>
    private void Said(Held held)
    {
        if (held.Run.SaidAt == held.Logged)
        {
            return;
        }

        held.Logged = held.Run.SaidAt;

        IReadOnlyList<TrackerSaid> said = held.Run.Said;

        if (said.Count == 0)
        {
            return;
        }

        RunProgress progress = held.Run.Progress();

        logger.LogInformation(
            "{Name}: {Answered} of {Asked} trackers answered with {Addresses} addresses; "
            + "{Seeds} seeds and {Leechers} leechers connected, {Choked} of them choking us, "
            + "{Askable} with something wanted; "
            + "the swarm has {SwarmSeeds} seeds and {SwarmPeers} peers; "
            + "the DHT knows {Nodes} nodes.",
            progress.Name ?? held.Name,
            said.Count(one => one.Peers is not null),
            said.Count,
            said.Sum(one => one.Peers ?? 0),
            progress.Seeds,
            progress.Leechers,

            // A peer that will not send anything and one that is merely slow
            // read the same in a peer count, and the difference is the whole
            // difference between a torrent that is stuck and one that is not.
            progress.ChokedBy,

            // And of the ones that will talk, how many hold anything this
            // client still wants. Nought here on a torrent standing still means
            // the swarm has nothing for it; anything above nought means the
            // fault is this client's.
            progress.Askable,
            held.Run.SwarmSeeds?.ToString(CultureInfo.InvariantCulture) ?? "an unknown number of",
            held.Run.SwarmPeers?.ToString(CultureInfo.InvariantCulture) ?? "an unknown number of",

            // Said every announce rather than once at boot. The one line there
            // was reported the table straight after the bootstrap - two routers
            // and the eight contacts each hands over - and was read, by me, as
            // a table that had stopped growing. Whether it does is a thing to
            // measure, so here it is measured.
            _dht?.Table.Count ?? 0);

        // Not one line per tracker that did not answer. The owner's decision of
        // 11 September 2026: a torrent carrying seventy trackers put up to
        // seventy lines into the server log on every announce, most of them the
        // same dead hosts, and the one line above already says how many of them
        // answered.
    }

    /// <summary>
    /// Stops a torrent whose contents are not worth a byte.
    /// </summary>
    /// <remarks>
    /// The metadata has arrived and there is no video file in it. That is what
    /// a fake release looks like from the inside — on 22 August 2026 one was a
    /// 1.2 GB executable named after an episode — and the whole of the defence
    /// is that this runs before any of it is asked for.
    /// </remarks>
    private void Refuse(Held held)
    {
        if (!held.Run.NothingWanted)
        {
            return;
        }

        held.Error = "There is no video file in it, so nothing in it was downloaded.";

        // The one refusal that is about the torrent rather than about tonight.
        // Nothing will ever put a video file into it, so it is never worth
        // asking for again.
        held.ErrorIsTheRelease = true;

        held.Run.Pause();

        logger.LogWarning("{Name} was refused: {Reason}", held.Run.Torrent?.Name ?? held.Name, held.Error);
        journal.Failed(ActivityStage.Download, held.Run.Torrent?.Name ?? held.Name ?? "a torrent", held.Error);
    }

    /// <summary>
    /// Stops a torrent the disk cannot hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The free-space rule of <c>S6-01</c> lives in <c>Grab.Room</c>, which
    /// only the search pipeline passes through. A magnet the owner pastes and a
    /// torrent handed back after the client lost it reach the engine without
    /// it — and neither could pass it, because a magnet has no size until its
    /// metadata arrives. So the same rule is applied here, at the moment the
    /// size is really known, whoever added it.
    /// </para>
    /// <para>
    /// The same disk holds the library and the database, so a torrent that
    /// fills it takes the media server down with it. That is the whole reason
    /// the rule exists.
    /// </para>
    /// <para>
    /// Measured against what will be fetched rather than what the torrent
    /// weighs: only the video files are downloaded, so a pack carrying a sample
    /// and a folder of screenshots is judged on its episodes.
    /// </para>
    /// </remarks>
    private void Cramped(Held held)
    {
        if (space is null || held.Error is not null || held.Run.Paused)
        {
            return;
        }

        RunProgress progress = held.Run.Progress();

        if (progress.BytesTotal is not long needed
            || space.FreeBytes(held.Run.Folder()) is not long free
            || free >= needed - progress.BytesDone)
        {
            return;
        }

        // Both numbers, because "not enough space" tells the owner nothing they
        // can act on and these two say exactly what to clear.
        held.Error =
            $"It needs {Size(needed - progress.BytesDone)} more and {held.Run.Folder()} has {Size(free)} free.";

        // About the torrent and not about tonight: no amount of waiting makes
        // it fit, and the owner has to do something before it can be asked for
        // again.
        held.ErrorIsTheRelease = true;

        held.Run.Pause();

        logger.LogWarning("{Name} was stopped: {Reason}", held.Run.Torrent?.Name ?? held.Name, held.Error);
        journal.Failed(ActivityStage.Download, held.Run.Torrent?.Name ?? held.Name ?? "a torrent", held.Error);
    }

    /// <summary>Bytes as an owner reads them.</summary>
    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = bytes;
        int unit = 0;

        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{size:0.#} {units[unit]}");
    }

    /// <summary>
    /// Gives up on a torrent that has stopped getting anywhere.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>StallMinutes</c> from settings: no progress <strong>and</strong> no
    /// peers for that long. Both halves, because a torrent moving slowly from
    /// one peer is not stalled and a torrent with forty peers waiting on the
    /// last piece is not either.
    /// </para>
    /// <para>
    /// Without it a magnet whose swarm has died sits on the Downloads page for
    /// as long as the server runs and the episode it was grabbed for is never
    /// looked for again. <c>StallWatch</c> was written for this in Sprint 6 and
    /// then wired to nothing at all, which is how fifteen of the owner's
    /// torrents came to sit at nought peers indefinitely.
    /// </para>
    /// </remarks>
    private void Stalled(Held held)
    {
        if (held.Error is not null || held.Run.Paused)
        {
            return;
        }

        RunProgress progress = held.Run.Progress();

        if (!held.Stall.Observe(progress.BytesDone, progress.Peers))
        {
            return;
        }

        // Its own words, and they name the limit so the owner knows which
        // setting to change.
        held.Error = $"Nothing arrived and no peer was connected for {stallLimit.TotalMinutes:0.#} minutes.";

        held.Run.Pause();

        logger.LogWarning("{Name} stalled: {Reason}", progress.Name ?? held.Name, held.Error);
        journal.Failed(ActivityStage.Download, progress.Name ?? held.Name ?? "a torrent", held.Error);
    }

    /// <summary>
    /// Stops seeding a torrent that has given back what was asked of it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SeedRatio</c> and <c>SeedHours</c> were on the Settings page and read
    /// by nothing, so a finished torrent stayed in its swarm for as long as the
    /// server ran.
    /// </para>
    /// <para>
    /// A public torrent is finished the moment it is complete: this client
    /// never uploads on a public swarm, so staying in one gives nothing to
    /// anybody while costing a connection. A private one seeds to the ratio or
    /// the hours, whichever comes first, because there the tracker keeps an
    /// account of what the owner has given back.
    /// </para>
    /// <para>
    /// Stopped rather than removed. The files stay where they are and staging
    /// takes them from there; removing the torrent would take the row off the
    /// Downloads page before the owner had seen it finish.
    /// </para>
    /// </remarks>
    private void Seeded(Held held, DateTimeOffset now)
    {
        if (held.Error is not null || held.Run.Paused || held.Run.Torrent is null)
        {
            return;
        }

        RunProgress progress = held.Run.Progress();

        if (!progress.Complete)
        {
            held.Finished = null;

            return;
        }

        held.Finished ??= now;

        double ratio = progress.Downloaded > 0 ? progress.Uploaded / (double)progress.Downloaded : 0;

        if (!seeding.Reached(held.Run.Torrent.Private, ratio, now - held.Finished.Value))
        {
            return;
        }

        held.Run.Pause();
    }

    /// <summary>
    /// Keeps no more than <c>MaxConcurrentDownloads</c> of them running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The setting existed and was read from the page and passed to nothing at
    /// all: on 22 August 2026 the owner's client had sixteen torrents dialling
    /// at once, which is sixteen swarms sharing one line and one set of
    /// sockets, and fifteen of them never got past fetching their metadata.
    /// </para>
    /// <para>
    /// Oldest first, so the queue is the order they were grabbed in and a
    /// torrent cannot be overtaken for ever by newer ones. A torrent that is
    /// finished does not hold a slot — it is seeding, not downloading — and a
    /// torrent the owner stopped keeps its place rather than being started
    /// again by this.
    /// </para>
    /// </remarks>
    private void Queue()
    {
        int running = 0;

        foreach (Held held in _torrents.Values
                     .Where(one => one.Error is null && (!one.Run.Paused || one.Queued))
                     .OrderBy(one => one.Since))
        {
            if (held.Run.Progress().Complete)
            {
                // Seeding, which costs a connection and not the download this
                // limit is about.
                continue;
            }

            if (running < maxConcurrent)
            {
                running++;

                if (held.Queued)
                {
                    held.Run.Resume();
                    held.Queued = false;
                }

                continue;
            }

            if (!held.Queued)
            {
                held.Run.Pause();
                held.Queued = true;
            }
        }
    }

    /// <summary>
    /// A torrent's metadata as it was last written down, or null.
    /// </summary>
    /// <remarks>
    /// Refused rather than trusted when it does not hash to the hash it is
    /// filed under: it is a file on disk, and the info hash is what the whole
    /// of BitTorrent's trust rests on.
    /// </remarks>
    private TorrentMetadata? Remembered(string infoHash, IReadOnlyList<string> trackers)
    {
        if (resume?.Recall(infoHash) is not byte[] info)
        {
            return null;
        }

        try
        {
            TorrentMetadata metadata = TorrentMetadata.FromInfo(info, trackers);

            if (string.Equals(metadata.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase))
            {
                return metadata;
            }

            logger.LogWarning(
                "The metadata kept for {Hash} is for {Other}, so it was ignored.",
                infoHash,
                metadata.InfoHash);
        }
        catch (TorrentFormatException unreadable)
        {
            logger.LogWarning("The metadata kept for {Hash} could not be read: {Reason}", infoHash, unreadable.Message);
        }

        return null;
    }

    /// <summary>One torrent as the pipeline is allowed to see it.</summary>
    private TorrentStatus Status(string infoHash, Held held)
    {
        RunProgress progress = held.Run.Progress();

        return new(
            infoHash,
            progress.Name ?? held.Name,
            State(held, progress),
            progress.BytesDone,
            progress.BytesTotal,
            progress.DownloadRateBytesPerSecond,
            progress.UploadRateBytesPerSecond,
            progress.Peers,
            progress.Seeds,

            // Nothing downloaded is not a ratio of nought: it is a ratio nobody
            // can work out, and drawing it as nought says this client has given
            // nothing back when it has taken nothing.
            progress.Downloaded > 0 ? progress.Uploaded / (double)progress.Downloaded : null,
            Eta(progress),
            held.Error,

            // What the trackers say the whole swarm holds. Nought connected out
            // of three hundred seeds is a client that has not met anybody yet;
            // nought out of nought is a dead release. One number cannot say
            // which, and the owner is reading it to decide whether to wait.
            held.Run.SwarmSeeds,
            held.Run.SwarmPeers,

            // Which kind of refusal this is. Set where the refusal is made and
            // never passed here, so it arrived false every time and nothing was
            // ever refused for ever: a torrent with no video file in it — a
            // 1.2 GB executable named after an episode, on 22 August 2026 —
            // came round again every six hours for as long as the plugin ran.
            held.ErrorIsTheRelease,

            // Bytes in, whole pieces or not. Drawn where it is ahead of what is
            // verified, which is the only case in which a torrent looks stopped
            // while it is not.
            progress.Downloaded,

            // Counted as leechers, never a total with the seeds taken off it.
            progress.Leechers);
    }

    /// <summary>Where one torrent stands, in the port's own words.</summary>
    /// <remarks>
    /// Fetching metadata is a state of its own and not a shade of downloading:
    /// a magnet has no file list until its metadata arrives, and reporting that
    /// as nought per cent downloading makes a torrent that will never resolve
    /// look like one about to start.
    /// </remarks>
    private static TorrentState State(Held held, RunProgress progress)
    {
        if (held.Error is not null)
        {
            return TorrentState.Error;
        }

        if (held.Run.Paused)
        {
            if (held.Queued)
            {
                return TorrentState.Queued;
            }

            // Complete and stopped is finished, not paused. Paused is the owner
            // having stopped something, and reading "paused" against a row at a
            // hundred per cent says the owner has to do something about it when
            // what it is waiting for is staging.
            return progress.Complete ? TorrentState.Finished : TorrentState.Paused;
        }

        if (!progress.HasMetadata)
        {
            return TorrentState.FetchingMetadata;
        }

        return progress.Complete ? TorrentState.Seeding : TorrentState.Downloading;
    }

    /// <summary>How long it has left, or null when that cannot be worked out.</summary>
    /// <remarks>
    /// Null while nothing is moving, rather than a number that grows to
    /// infinity as the rate falls to nothing: "4,294,967,295 hours left" is
    /// worse than saying it is not known.
    /// </remarks>
    private static TimeSpan? Eta(RunProgress progress)
    {
        if (progress.BytesTotal is not long total || progress.DownloadRateBytesPerSecond <= 0)
        {
            return null;
        }

        long left = total - progress.BytesDone;

        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(left / progress.DownloadRateBytesPerSecond);
    }

    /// <summary>
    /// Everything one torrent wrote, and nothing anybody else did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing here throws. Removing a torrent has already happened by the time
    /// the files are reached, and something that cannot be deleted must not
    /// undo it or take the caller down.
    /// </para>
    /// <para>
    /// <strong>This deleted the download folder.</strong> It was handed
    /// <c>Run.Folder()</c>, which is the folder every torrent downloads into,
    /// and emptied it recursively — so finishing one grab, or the owner
    /// cancelling one download, took every other download on the machine with
    /// it. On 2 September 2026 the owner's folder held two torrents and three
    /// resume files, and one grab being finished with left one folder and
    /// nothing else.
    /// </para>
    /// <para>
    /// What belongs to a torrent is its own file list. A torrent of several
    /// files puts them in a folder of its own name, and everything under that
    /// folder is its own — including the text files a release ships with, which
    /// were never downloaded and would otherwise be left behind. A torrent of
    /// one file wrote one file.
    /// </para>
    /// <para>
    /// <strong>A name is not to be trusted with a path.</strong> The folder is
    /// resolved and checked to be under the download folder before anything is
    /// deleted: a torrent that calls itself <c>..</c> otherwise names the
    /// owner's disk.
    /// </para>
    /// </remarks>
    private void Delete(string folder, TorrentMetadata? torrent, string infoHash)
    {
        if (torrent is null)
        {
            // Nothing of its own can be on disk. The files are created when the
            // session is opened and a session cannot be opened without the file
            // list, so a torrent that never had metadata has written nothing —
            // and the download folder is every torrent's, never this one's to
            // delete.
            logger.LogInformation("{Hash} was removed, and it had written nothing to delete.", infoHash);

            return;
        }

        try
        {
            // Every file the torrent names, and nothing it does not. The folder of a many-file torrent used to
            // be deleted whole, so whatever somebody else had put in it went too — and the owner's rule of
            // 16 September 2026 is that this plugin deletes only what it created itself.
            HashSet<string> folders = new(StringComparer.OrdinalIgnoreCase);

            foreach (TorrentFileEntry file in torrent.Files)
            {
                string path = Path.Combine(
                    folder,
                    torrent.PathUnderFolder(file).Replace('/', Path.DirectorySeparatorChar));

                if (!Inside(folder, path))
                {
                    continue;
                }

                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                for (string? parent = Path.GetDirectoryName(path); parent is not null && Inside(folder, parent); parent = Path.GetDirectoryName(parent))
                {
                    folders.Add(parent);
                }
            }

            // Then the folders the torrent's files were in, deepest first, and only once nothing is left in
            // them: a folder still holding something the torrent never named is not this plugin's to remove.
            foreach (string made in folders.OrderByDescending(one => one.Length))
            {
                if (Directory.Exists(made) && !Directory.EnumerateFileSystemEntries(made).Any())
                {
                    Directory.Delete(made);
                }
            }
        }
        catch (Exception wrong) when (wrong is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Hash} was removed and its files could not be deleted: {Reason}", infoHash, wrong.Message);
        }
    }

    /// <summary>Whether a path is really under the download folder.</summary>
    /// <remarks>
    /// Resolved rather than compared as text, because a torrent's own name goes
    /// into it and a name is whatever the person who made the torrent typed.
    /// </remarks>
    private static bool Inside(string folder, string path)
    {
        string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(path);

        return full.Length > root.Length && full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>One torrent this client is holding, and what only it knows.</summary>
    /// <remarks>
    /// The run knows everything about the torrent; this knows what the client
    /// knows about the run — when it was taken on, what it was called before
    /// anybody knew its real name, and why it was given up on.
    /// </remarks>
    private sealed class Held(TorrentRun run, string? name, DateTimeOffset since, StallWatch stall)
    {
        public TorrentRun Run => run;

        /// <summary>Whether it has stopped getting anywhere.</summary>
        public StallWatch Stall => stall;

        /// <summary>What the magnet called it, until the metadata says better.</summary>
        public string? Name => name;

        /// <summary>When its clock started, which a resume restarts.</summary>
        public DateTimeOffset Since { get; set; } = since;

        /// <summary>Why it was given up on, in the client's own words.</summary>
        public string? Error { get; set; }

        /// <summary>Whether that reason is about the release rather than the moment.</summary>
        public bool ErrorIsTheRelease { get; set; }

        /// <summary>Which announce has already been written down.</summary>
        public DateTimeOffset Logged { get; set; }

        /// <summary>Whether it is stopped because the client is full, not because the owner stopped it.</summary>
        public bool Queued { get; set; }

        /// <summary>When it finished downloading, which is when seeding started.</summary>
        public DateTimeOffset? Finished { get; set; }

        /// <summary>The one decision this torrent has outstanding, and when it falls due.</summary>
        /// <remarks>
        /// One at a time, because only one can ever be next: a torrent is
        /// waiting for metadata, or downloading, or seeding out the owner's
        /// hours. Cancelled and set again whenever anything really happens, so
        /// on a download that is running it never goes off at all.
        /// </remarks>
        public ITimer? Wake { get; set; }

        /// <summary>When <see cref="Wake"/> was last set, so it is not reset per piece.</summary>
        public DateTimeOffset Armed { get; set; }
    }
}
