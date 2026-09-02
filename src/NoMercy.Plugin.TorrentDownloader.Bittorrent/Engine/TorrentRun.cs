using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Opening a conversation with one peer.
/// </summary>
/// <remarks>
/// The seam the sockets sit behind. Whether to dial a peer at all, how many at
/// once, and what to do with one that will not talk are decided above it and
/// tested without a network; below it is a TCP connect, an encryption
/// negotiation and a handshake, and none of that has anything to decide.
/// </remarks>
public interface IPeerDialler
{
    /// <summary>
    /// Connects and introduces this client, or answers nothing.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception for a peer that would not talk: most of
    /// the addresses a tracker hands out are stale, and a dead peer is the
    /// ordinary case rather than a fault.
    /// </remarks>
    Task<PeerConnection?> DialAsync(
        PeerAddress peer,
        byte[] infoHash,
        byte[] peerId,
        int pieces,
        CancellationToken ct);
}

/// <summary>How one tracker answered an announce.</summary>
/// <param name="Tracker">Its address.</param>
/// <param name="Peers">How many addresses it handed over, or null if it did not answer.</param>
/// <param name="Failure">Why it did not, or null.</param>
public sealed record TrackerSaid(string Tracker, int? Peers, string? Failure);

/// <summary>
/// Where one torrent stands, from the driver's side.
/// </summary>
/// <param name="Name">What the metadata calls it, or null before it has arrived.</param>
/// <param name="HasMetadata">Whether the file list is known.</param>
/// <param name="BytesDone">How much is verified on disk.</param>
/// <param name="BytesTotal">How big it is, or null while nothing knows.</param>
/// <param name="Peers">How many connections are up.</param>
/// <param name="Seeds">How many of those have the lot.</param>
/// <param name="Downloaded">Bytes of pieces that have arrived.</param>
/// <param name="Uploaded">Bytes of pieces that have gone out.</param>
/// <param name="DownloadRateBytesPerSecond">
/// How fast it is moving now, measured between the last two readings and never
/// averaged over the whole transfer.
/// </param>
/// <param name="UploadRateBytesPerSecond">The same, going out.</param>
/// <param name="Complete">Whether every piece is verified.</param>
/// <param name="SwarmSeeds">
/// How many seeds the trackers say the whole swarm has, or null before one
/// answered. Not what this client is connected to: nought connected out of
/// three hundred is a client that has not met anybody yet, and nought out of
/// nought is a dead release.
/// </param>
/// <param name="SwarmPeers">The same for the peers still downloading it.</param>
public sealed record RunProgress(
    string? Name,
    bool HasMetadata,
    long BytesDone,
    long? BytesTotal,
    int Peers,
    int Seeds,
    long Downloaded,
    long Uploaded,
    double DownloadRateBytesPerSecond,
    double UploadRateBytesPerSecond,
    bool Complete,
    int? SwarmSeeds = null,
    int? SwarmPeers = null);

/// <summary>
/// One torrent, running: the parts of this client joined to the outside.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="TorrentSession"/> owns no sockets on purpose — a connection
/// arrives already introduced — and Sprint 5 left nothing that opens one. This
/// is that: it announces to the trackers, dials the peers they name, fetches
/// the metadata from a peer when all it started with was a magnet, opens the
/// disk and hands each connection to the session.
/// </para>
/// <para>
/// One peer that misbehaves costs that peer. Every connection runs on its own,
/// and a torrent with forty peers must not be taken down by the one that hung
/// up mid-message.
/// </para>
/// </remarks>
public sealed class TorrentRun : IDisposable
{
    private readonly byte[] _infoHash;
    private readonly List<string> _trackers;
    private readonly string _folder;
    private readonly TrackerSet _trackerSet;
    private readonly IPeerDialler _dialler;
    private readonly byte[] _peerId;
    private readonly int _listenPort;
    private readonly TimeProvider _time;
    private readonly ResumeKeeper? _resume;

    /// <summary>
    /// Which of the torrent's files are downloaded, or null for all of them.
    /// </summary>
    /// <remarks>
    /// Handed in rather than decided here. This engine deals in pieces and has
    /// no opinion about file types; what a video file is belongs to the plugin,
    /// which is the only thing that knows the owner's rule.
    /// </remarks>
    private readonly Func<IReadOnlyList<TorrentFileEntry>, IReadOnlyList<TorrentFileEntry>>? _choose;

    /// <summary>The owner's rate limits, shared with every other torrent.</summary>
    private readonly RateLimits? _limits;
    private readonly RateMeter _down;
    private readonly RateMeter _up;
    private readonly Lock _lock = new();

    /// <summary>
    /// Every address this run has dialled, and when it last did.
    /// </summary>
    /// <remarks>
    /// A peer already tried is one the next announce names again. Re-dialling
    /// it every interval is a connection a minute to the same machine, which is
    /// how a client gets itself banned by a swarm it is trying to join.
    ///
    /// A clock rather than a set, and that is the fault this carries the mark
    /// of. As a set it barred an address for the life of the torrent, so a run
    /// that lost the peers of its first announce could never replace them:
    /// every later announce named the same addresses and every one of them was
    /// already in the set. Only a pause and a resume, which cleared it, brought
    /// such a torrent back.
    /// </remarks>
    private readonly Dictionary<string, DateTimeOffset> _tried = new(StringComparer.Ordinal);

    /// <summary>Every address this run has heard of, from whatever named it.</summary>
    /// <remarks>
    /// A tracker is asked at its own interval, and a run that has lost every
    /// peer cannot wait that interval out with nobody to talk to. The addresses
    /// of the last announce are still addresses, so a pass between announces
    /// offers them again rather than having nowhere to look.
    /// </remarks>
    private readonly Dictionary<string, PeerAddress> _known = new(StringComparer.Ordinal);

    /// <summary>Which address each dialled peer is on, while it is connected.</summary>
    /// <remarks>
    /// So that a peer this run is in the middle of talking to is not dialled a
    /// second time when the next announce names it again. A connection knows
    /// its socket rather than the address it was reached at, and a peer that
    /// dialled in was never dialled out to, so neither is in here.
    /// </remarks>
    private readonly Dictionary<PeerConnection, string> _talkingTo = [];

    /// <summary>When this run last announced.</summary>
    /// <remarks>
    /// Kept here rather than by the caller, so a pass can come round sooner
    /// than the tracker's interval without announcing on every one of them.
    /// </remarks>
    private DateTimeOffset _announced = DateTimeOffset.MinValue;

    private readonly List<PeerConnection> _peers = [];

    /// <summary>The conversations still going.</summary>
    /// <remarks>
    /// Pruned as it is added to, because nothing else ever takes anything out
    /// of it and nothing reads it. Left to grow it kept a task for every peer
    /// this run ever spoke to: fifty dials every half minute on a torrent that
    /// runs for a week is a list nobody looks at, holding a hundred thousand
    /// completed tasks.
    ///
    /// It is kept at all so a conversation is rooted while it runs, and so
    /// anything that wants to wait for them has something to wait on.
    /// </remarks>
    private readonly List<Task> _conversations = [];

    /// <summary>Completes the first time the metadata is whole and hashes right.</summary>
    private readonly TaskCompletionSource _metadata = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>
    /// The metadata being collected, shared across every peer.
    /// </summary>
    /// <remarks>
    /// One fetch for the torrent rather than one per peer: the pieces are
    /// sixteen kibibytes each and a large torrent has thirty of them, so asking
    /// every peer for all of them is thirty times the traffic for one answer.
    /// </remarks>
    private MetadataFetch? _fetch;

    private TorrentSession? _session;
    private TorrentDisk? _disk;
    private TorrentMetadata? _torrent;
    private bool _disposed;

    /// <summary>
    /// Stops what this run started on its own account.
    /// </summary>
    /// <remarks>
    /// A peer exchange arrives inside a conversation and is dialled outside
    /// one, so those dials answer to the run rather than to whichever caller
    /// happened to be reading a message. Cancelled when the run is disposed,
    /// so a torrent that is removed does not go on meeting people.
    /// </remarks>
    private readonly CancellationTokenSource _stopping = new();

    /// <summary>Where peers come from when the tracker's list runs thin.</summary>
    /// <remarks>
    /// Null where the client has none. A tracker hands out fifty addresses and
    /// most are stale; the DHT is how every other client finds the hundreds
    /// that are actually there.
    /// </remarks>
    private readonly Dht? _dht;

    private bool _nothingWanted;

    /// <summary>What the last announce got back, one line per tracker.</summary>
    /// <remarks>
    /// Kept because nothing else could say it. A torrent downloading at ten
    /// megabytes a second wrote no line at all, and one sitting at nought wrote
    /// the same nothing — so which trackers answered, how many addresses came
    /// back and which of them were dead was knowable only by running a probe
    /// beside the plugin. That is what an owner should be able to read.
    /// </remarks>
    private IReadOnlyList<TrackerSaid> _said = [];

    private DateTimeOffset _saidAt;

    /// <summary>
    /// What the trackers say the whole swarm holds, as against what this client
    /// is connected to.
    /// </summary>
    /// <remarks>
    /// An announce answers with both counts and they were read for the interval
    /// and thrown away. They are the numbers that say whether a download is
    /// worth waiting for: nought connected out of three hundred seeds is a
    /// client that has not met anybody yet, and nought out of nought is a dead
    /// release. Drawn as the same one number, those two look identical.
    ///
    /// The largest any tracker gave, because a tracker knows only its own
    /// members and the swarm is at least as big as the best-informed one says.
    /// </remarks>
    private int? _swarmSeeds;

    private int? _swarmPeers;

    /// <summary>What decides which pieces are already on disk.</summary>
    /// <remarks>
    /// The real one reads and hashes them. A test hands in its own, because the
    /// rule worth holding this to — that nothing waits on it — cannot be shown
    /// against a pass that is over before anything else can look.
    /// </remarks>
    private readonly Func<TorrentMetadata, TorrentDisk, Bitfield>? _verify;

    /// <summary>Whether this run is opening its session, which reads the disk.</summary>
    /// <remarks>
    /// So that the second caller does not start a second pass over the same
    /// files, and so that nothing waits on the first: opening is done with the
    /// lock let go, and this is what says it is under way.
    /// </remarks>
    private bool _verifying;

    /// <summary>Set while nobody is opening the session, so a caller can wait on it.</summary>
    private readonly ManualResetEventSlim _opened = new(true);

    /// <summary>How many peers one DHT search asks for.</summary>
    /// <remarks>
    /// The same fifty a tracker is asked for. More than a swarm's worth of
    /// dials at once is a burst this client has no use for.
    /// </remarks>
    private const int DhtPeersWanted = 50;

    public TorrentRun(
        byte[] infoHash,
        IReadOnlyList<string> trackers,
        string folder,
        TrackerSet trackerSet,
        IPeerDialler dialler,
        byte[] peerId,
        int listenPort,
        TimeProvider time,
        TorrentMetadata? torrent = null,
        ResumeKeeper? resume = null,
        Func<IReadOnlyList<TorrentFileEntry>, IReadOnlyList<TorrentFileEntry>>? choose = null,
        RateLimits? limits = null,
        Dht? dht = null,
        Func<TorrentMetadata, TorrentDisk, Bitfield>? verify = null)
    {
        _verify = verify;
        _dht = dht;
        _infoHash = infoHash;
        _trackers = [.. trackers];
        _folder = folder;
        _trackerSet = trackerSet;
        _dialler = dialler;
        _peerId = peerId;
        _listenPort = listenPort;
        _time = time;
        _torrent = torrent;
        _resume = resume;
        _choose = choose;
        _limits = limits;
        _down = new(time);
        _up = new(time);
    }

    /// <summary>The metadata, once anything knows it.</summary>
    public TorrentMetadata? Torrent
    {
        get
        {
            lock (_lock)
            {
                return _torrent;
            }
        }
    }

    /// <summary>How each tracker answered the last announce.</summary>
    public IReadOnlyList<TrackerSaid> Said
    {
        get
        {
            lock (_lock)
            {
                return _said;
            }
        }
    }

    /// <summary>When that announce was, so a caller can say a line once.</summary>
    public DateTimeOffset SaidAt
    {
        get
        {
            lock (_lock)
            {
                return _saidAt;
            }
        }
    }

    /// <summary>What the trackers say the swarm holds, or null before one answered.</summary>
    public int? SwarmSeeds
    {
        get
        {
            lock (_lock)
            {
                return _swarmSeeds;
            }
        }
    }

    /// <summary>The same for the peers still downloading it.</summary>
    public int? SwarmPeers
    {
        get
        {
            lock (_lock)
            {
                return _swarmPeers;
            }
        }
    }

    /// <summary>Whether this run is stopped and dialling nobody.</summary>
    public bool Paused { get; private set; }

    /// <summary>
    /// Whether the metadata arrived and held nothing worth downloading.
    /// </summary>
    /// <remarks>
    /// A torrent with no video file in it. The caller stops it and says so —
    /// it is the shape a fake release takes, and on 22 August 2026 one of them
    /// was a 1.2 GB executable named after an episode.
    /// </remarks>
    public bool NothingWanted
    {
        get
        {
            // Asked for rather than waited for: nothing decides which files
            // are wanted until something asks for the session.
            //
            // Outside the lock, because opening one reads every byte already on
            // disk. Asked for in here it would hold the lock across that pass
            // just as surely as doing the reading inside it did, and everything
            // that asks this run anything would wait on it.
            Session();

            lock (_lock)
            {
                return _nothingWanted;
            }
        }
    }

    /// <summary>
    /// How long to leave it before announcing again, until a tracker has said.
    /// </summary>
    /// <remarks>
    /// Thirty minutes is what a tracker that publishes no interval is treated
    /// as asking for. It is the shipped default and nothing else: every tracker
    /// this client has ever been captured answering does publish one.
    /// </remarks>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    /// <summary>
    /// How long an address that this run is not connected to is left alone
    /// before it is dialled again.
    /// </summary>
    /// <remarks>
    /// Half a minute, which is the owner's number and not a guess. A floor
    /// there has to be — a peer exchange arrives about once a minute from every
    /// connected peer and names the same addresses each time, so with none at
    /// all one dead address would be dialled on every message that mentioned
    /// it. Half a minute is as short as that floor goes while still being a
    /// floor, and a torrent showing nought peers is a download not happening
    /// for as long as it says so.
    ///
    /// It costs only addresses this run is not connected to: a peer already
    /// being talked to is refused whatever the clock says.
    /// </remarks>
    public static readonly TimeSpan RedialAfter = TimeSpan.FromSeconds(30);

    /// <summary>
    /// How long a run with nobody to talk to waits before its next pass.
    /// </summary>
    /// <remarks>
    /// Not the same number as the redial floor, which is about one address:
    /// this is about the whole run, and a run with no peers is a run doing
    /// nothing at all. A minute, so that a torrent which has just lost everyone
    /// is looking again while the owner is still on the page, rather than
    /// sitting out the tracker's interval — a quarter of an hour at best and
    /// half an hour by default. The announce keeps that interval regardless, so
    /// the tracker is asked for nothing extra.
    /// </remarks>
    public static readonly TimeSpan LookAgainAfter = TimeSpan.FromMinutes(1);

    /// <summary>How many peers this run wants to be connected to at once.</summary>
    /// <remarks>
    /// Peer exchange in a large swarm names hundreds of addresses within
    /// minutes, and dialling all of them is hundreds of sockets for one
    /// torrent. Fifty is what this client asks a swarm for, and no pass dials
    /// past it.
    /// </remarks>
    public const int PeersWanted = 50;

    /// <summary>
    /// When to announce again, as the trackers themselves asked.
    /// </summary>
    /// <remarks>
    /// docs/06-torrent-client.md: announce at the tracker's own interval. The
    /// shortest interval anybody asked for, raised to the longest floor anybody
    /// set — a floor is what earns a ban when it is breached, so it wins over
    /// another tracker's wish to be asked sooner.
    /// </remarks>
    public TimeSpan Interval { get; private set; } = DefaultInterval;

    /// <summary>How long before this run's next pass.</summary>
    /// <remarks>
    /// The tracker's interval while there is somebody to talk to, and a minute
    /// while there is not. A run with no peers has nothing to do but look for
    /// some, and half an hour of having nothing to do is what an owner sees as
    /// nought per cent in front of a swarm somebody else can see three hundred
    /// seeds in. The announce keeps the tracker's own interval whatever this
    /// says, so coming round sooner asks the tracker for nothing.
    /// </remarks>
    public TimeSpan Wait
    {
        get
        {
            lock (_lock)
            {
                return _peers.Count > 0 ? Interval : LookAgainAfter;
            }
        }
    }

    /// <summary>
    /// Completes when the metadata has arrived and hashed right.
    /// </summary>
    /// <remarks>
    /// A torrent added from a magnet has no name, no size and no files until
    /// this does — which is why <c>FetchingMetadata</c> is a state of its own
    /// rather than nought per cent of downloading.
    /// </remarks>
    public Task Metadata => _metadata.Task;

    /// <summary>Where it stands, with every number real or absent.</summary>
    public RunProgress Progress()
    {
        lock (_lock)
        {
            if (_session is null || _torrent is null)
            {
                // No metadata means no size and no piece count, and a
                // percentage of a size nobody knows is a number made up on the
                // page. Peers are still real: they are what the metadata will
                // come from.
                return new(
                    _torrent?.Name,
                    _torrent is not null,
                    0,
                    _torrent?.TotalLength,
                    _peers.Count,
                    0,
                    0,
                    0,
                    0,
                    0,
                    false);
            }

            SessionProgress progress = _session.Progress();

            return new(
                _torrent.Name,
                true,
                progress.BytesDone,

                // What is being downloaded, not what the torrent weighs. Only
                // the video files are fetched, so a percentage against the
                // whole would stop short of a hundred on every torrent that
                // carries anything else.
                progress.WantedBytes,
                progress.Peers,
                progress.Seeds,
                progress.Downloaded,
                progress.Uploaded,

                // Measured between this reading and the last, which is what
                // makes a stalled torrent read as nought rather than as its
                // average since it started.
                _down.Measure(progress.Downloaded),
                _up.Measure(progress.Uploaded),
                progress.Complete);
        }
    }

    /// <summary>Every file in it, or nothing while the metadata has not arrived.</summary>
    public IReadOnlyList<TorrentFileEntry> Files => Torrent?.Files ?? [];

    /// <summary>Every tracker known for it.</summary>
    public IReadOnlyList<string> Trackers
    {
        get
        {
            lock (_lock)
            {
                return [.. _trackers];
            }
        }
    }

    /// <summary>Where its bytes land.</summary>
    public string Folder() => _folder;

    /// <summary>
    /// Takes on more trackers for the same torrent.
    /// </summary>
    /// <remarks>
    /// The same torrent from a second site is one torrent with more trackers,
    /// and more trackers is a faster download — which is the whole reason every
    /// indexer is asked. Without duplicates, or it would announce twice to the
    /// same host every interval.
    /// </remarks>
    public void Add(IEnumerable<string> more)
    {
        lock (_lock)
        {
            foreach (string tracker in more)
            {
                if (!_trackers.Contains(tracker, StringComparer.OrdinalIgnoreCase))
                {
                    _trackers.Add(tracker);
                }
            }
        }
    }

    /// <summary>
    /// One pass: announce, and dial whoever is new.
    /// </summary>
    /// <remarks>
    /// A pass rather than a loop, so the caller decides the interval and a test
    /// can run exactly one. The connections it opens outlive it — each runs
    /// until its peer goes or the run is stopped.
    /// </remarks>
    public async Task OnceAsync(CancellationToken ct)
    {
        if (Paused || _disposed)
        {
            return;
        }

        // Before the announce, not when a peer turns up: a client that knows
        // what the torrent is has somewhere for the first block to go, and the
        // files have to exist before the resume file can be judged against
        // them.
        Session();

        // The announce keeps the tracker's own interval; the pass around it
        // comes round sooner. A run that has lost every peer has to be able to
        // look for more without announcing again - asking oftener than the
        // tracker said is how a client gets itself banned, and sitting out a
        // half-hour interval with nobody to talk to is how a torrent shows
        // nought per cent in front of a swarm of three hundred seeds.
        if (_time.GetUtcNow() - _announced >= Interval)
        {
            _announced = _time.GetUtcNow();

            IReadOnlyList<TrackerResult> answers = await _trackerSet
                .AnnounceAsync(_trackers, Request(), ct)
                .ConfigureAwait(false);

            Told(answers);

            _said = [.. answers.Select(one => new TrackerSaid(
                one.Tracker,
                one.Response?.Peers.Count,
                one.Failure))];

            _saidAt = _time.GetUtcNow();

            Remember(answers
                .Where(one => one.Response is not null)
                .SelectMany(one => one.Response!.Peers));
        }

        // And the DHT, which is where the peers a tracker does not name are.
        // Only once the metadata has arrived: whether a torrent is private is
        // written in its info dictionary, and BEP 27 says a private one is
        // never to be looked for anywhere but its own tracker. A magnet has no
        // info dictionary yet, so until it does this asks nobody.
        //
        // Searched and not announced. An announce puts this client on the
        // DHT's own list of who has the file, which is the owner's rule about
        // not offering anything back on a public swarm — and a search costs
        // that swarm nothing.
        if (_dht is not null && Torrent is { Private: false } known)
        {
            _ = FromTheDhtAsync(known, ct);
        }

        // Everybody this run has ever heard of, not only whoever this announce
        // named: a pass that did not announce has nowhere else to look, and the
        // peers of the last announce are still peers.
        await DialEveryAsync(Pick(Book()), ct).ConfigureAwait(false);
    }

    /// <summary>Every address this run knows of.</summary>
    private List<PeerAddress> Book()
    {
        lock (_lock)
        {
            // Least recently tried first, and never tried before any of them.
            // A pass dials at most PeersWanted, so walked in the order they
            // arrived the same first fifty were offered every time and the rest
            // were never reached: a tracker hands its addresses over in
            // whatever order it likes and most of them are stale, so fifty dead
            // ones at the front is a torrent that redials them for the rest of
            // its life and never tries the ones that would have answered.
            return
            [
                .. _known
                    .OrderBy(one => _tried.TryGetValue(one.Key, out DateTimeOffset when)
                        ? when
                        : DateTimeOffset.MinValue)
                    .Select(one => one.Value),
            ];
        }
    }

    /// <summary>Writes addresses into the book, whoever named them.</summary>
    private void Remember(IEnumerable<PeerAddress> addresses)
    {
        lock (_lock)
        {
            foreach (PeerAddress address in addresses)
            {
                _known[address.ToString()] = address;
            }
        }
    }

    /// <summary>
    /// Which of a set of addresses are worth dialling on this pass.
    /// </summary>
    /// <remarks>
    /// The same peer from two trackers is one peer: dialling it twice is two
    /// connections to one client, which is how a swarm of six comes to look
    /// like a swarm of twelve. <see cref="Worth"/> stamps each address as it
    /// admits it, so the second copy in one pass is refused along with the ones
    /// dialled too recently.
    /// </remarks>
    private List<PeerAddress> Pick(IReadOnlyList<PeerAddress> addresses)
    {
        List<PeerAddress> fresh = [];

        lock (_lock)
        {
            int room = PeersWanted - _peers.Count;

            foreach (PeerAddress address in addresses)
            {
                if (fresh.Count >= room)
                {
                    break;
                }

                if (Worth(address))
                {
                    fresh.Add(address);
                }
            }
        }

        return fresh;
    }

    /// <summary>
    /// Whether an address is worth dialling now, recording that it was.
    /// </summary>
    /// <remarks>
    /// Called while <see cref="_lock"/> is held: it reads who this run is
    /// connected to and writes when the address was last tried.
    /// </remarks>
    private bool Worth(PeerAddress address)
    {
        string key = address.ToString();

        // A peer this run is talking to already. One machine on two connections
        // is not two peers, and both ends carry the second one for nothing.
        if (_talkingTo.ContainsValue(key))
        {
            return false;
        }

        DateTimeOffset now = _time.GetUtcNow();

        if (_tried.TryGetValue(key, out DateTimeOffset last) && now - last < RedialAfter)
        {
            return false;
        }

        _tried[key] = now;

        return true;
    }

    /// <summary>
    /// Keeps a conversation, and forgets the ones that are over.
    /// </summary>
    /// <remarks>
    /// Called while <see cref="_lock"/> is held. A conversation ends in its own
    /// finally and tells nobody, so this is the only place that can notice.
    /// </remarks>
    private void Talking(Task conversation)
    {
        _conversations.RemoveAll(one => one.IsCompleted);
        _conversations.Add(conversation);
    }

    /// <summary>Dials a set of addresses and takes on whoever answers.</summary>
    /// <remarks>
    /// The dials are awaited and the conversations are not. A dial has an end -
    /// the peer answers or it does not - and a conversation lasts as long as
    /// the peer does, so a pass that waited for one would never come round
    /// again.
    /// </remarks>
    private async Task DialEveryAsync(IReadOnlyList<PeerAddress> fresh, CancellationToken ct)
    {
        if (fresh.Count == 0)
        {
            return;
        }

        (PeerAddress Address, PeerConnection? Peer)[] dialled = await Task
            .WhenAll(fresh.Select(async one => (one, await DialAsync(one, ct).ConfigureAwait(false))))
            .ConfigureAwait(false);

        foreach ((PeerAddress address, PeerConnection? peer) in dialled)
        {
            if (peer is null)
            {
                continue;
            }

            lock (_lock)
            {
                _peers.Add(peer);
                _talkingTo[peer] = address.ToString();

                Talking(ConverseAsync(peer, ct));
            }
        }
    }

    /// <summary>
    /// Takes on a peer that dialled in rather than being dialled.
    /// </summary>
    /// <remarks>
    /// It is a peer like any other from here on. A paused run refuses it: a
    /// torrent the owner stopped that went on answering peers is not stopped,
    /// whatever the page says.
    /// </remarks>
    public void Take(PeerConnection peer, CancellationToken ct)
    {
        lock (_lock)
        {
            if (Paused || _disposed)
            {
                peer.Dispose();

                return;
            }

            _peers.Add(peer);

            Talking(ConverseAsync(peer, ct));
        }
    }

    /// <summary>Stops dialling and drops every connection, keeping the pieces.</summary>
    /// <remarks>
    /// The verified pieces and the disk stay exactly as they are: that is what
    /// makes resuming cost nothing. What goes is the conversations, because a
    /// paused torrent that kept answering peers is not paused.
    /// </remarks>
    public void Pause()
    {
        lock (_lock)
        {
            Paused = true;

            foreach (PeerConnection peer in _peers)
            {
                peer.Dispose();
            }

            _peers.Clear();
            _talkingTo.Clear();
        }
    }

    /// <summary>Starts dialling again from what is already verified.</summary>
    public void Resume()
    {
        lock (_lock)
        {
            Paused = false;

            // Every address is offered again, because the peers that were
            // dropped are the ones most likely still to be there.
            _tried.Clear();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (PeerConnection peer in _peers)
            {
                peer.Dispose();
            }

            _peers.Clear();
            _talkingTo.Clear();
            _session?.Dispose();
            _session = null;
            _disk = null;
        }

        // Let go of anybody waiting on the session before the token is
        // cancelled: a run being disposed while it is being opened must not
        // leave a caller sitting on the half hour.
        _opened.Set();

        // Outside the lock: cancelling runs continuations, and one of them
        // taking this lock on the way out would deadlock against it.
        _stopping.Cancel();
        _stopping.Dispose();
        _opened.Dispose();
    }

    /// <summary>
    /// Takes the trackers' word for when to come back.
    /// </summary>
    /// <remarks>
    /// The shortest interval anybody asked for, raised to the longest floor
    /// anybody set. Breaching a floor is what earns a ban, so it wins over
    /// another tracker's wish to be asked sooner; and a tracker that refused
    /// asked for nothing, so it has no say in either.
    /// </remarks>
    private void Told(IReadOnlyList<TrackerResult> answers)
    {
        AnnounceResponse[] said = [.. answers.Select(one => one.Response).OfType<AnnounceResponse>()];

        if (said.Length == 0)
        {
            return;
        }

        _swarmSeeds = said.Select(one => one.Seeders).OfType<int>().DefaultIfEmpty().Max() is int seeds and > 0
            ? seeds
            : _swarmSeeds;

        _swarmPeers = said.Select(one => one.Leechers).OfType<int>().DefaultIfEmpty().Max() is int peers and > 0
            ? peers
            : _swarmPeers;

        TimeSpan wanted = said
            .Select(one => one.Interval)
            .Where(one => one > TimeSpan.Zero)
            .DefaultIfEmpty(DefaultInterval)
            .Min();

        TimeSpan floor = said
            .Select(one => one.MinInterval ?? TimeSpan.Zero)
            .DefaultIfEmpty(TimeSpan.Zero)
            .Max();

        Interval = wanted > floor ? wanted : floor;
    }

    /// <summary>
    /// What is announced as still wanted before the metadata says otherwise.
    /// </summary>
    /// <remarks>
    /// Any non-zero number gets peers; this one is a terabyte so that no
    /// tracker ranking by need reads a client that knows nothing as one that
    /// is nearly finished.
    /// </remarks>
    private const long UnknownSize = 1L << 40;

    /// <summary>What this client tells a tracker about itself.</summary>
    private AnnounceRequest Request()
    {
        RunProgress progress = Progress();

        return new(
            _infoHash,
            _peerId,
            _listenPort,
            progress.Downloaded,
            progress.Uploaded,

            // What is left, or an unfinished amount when nobody knows the size
            // yet. A tracker reads a left of nought as a seed, and a seed is
            // sent no peers because it has no use for any — so announcing
            // nought before the metadata arrives asks every tracker for the
            // one thing that cannot help and is answered, correctly, with an
            // empty list and no error at all.
            //
            // That is what left every magnet at "fetching metadata" with no
            // peer and no seed until it timed out: announcing worked, the
            // swarm was there — 1206 seeders on the release the owner pasted
            // by hand — and this client had told every tracker it was done.
            //
            // A terabyte because the number has to be large as well as
            // non-zero: a tracker that ranks by how much a peer still needs
            // must not read a client that knows nothing as one that is nearly
            // finished.
            progress.BytesTotal is long total ? Math.Max(0, total - progress.BytesDone) : UnknownSize,
            AnnounceEvent.Started);
    }

    /// <summary>
    /// Dials peers this run heard about from somewhere other than a tracker.
    /// </summary>
    /// <remarks>
    /// A tracker hands out fifty addresses and most of them are stale, so a
    /// client with nowhere else to ask ends up talking to one or two. Everybody
    /// else in the swarm is known to the peers already connected, and peer
    /// exchange is how they say so — on 26 August 2026 a swarm other clients
    /// saw hundreds of seeds in gave this one a single peer.
    /// </remarks>
    private async Task MeetAsync(IReadOnlyList<PeerAddress> addresses, CancellationToken ct)
    {
        // The same peer twice is one peer, whoever named it: a tracker and a
        // peer exchange naming the same address is the ordinary case rather
        // than a second client. Remembered whether or not it is dialled now, so
        // that a later pass still has it.
        Remember(addresses);

        await DialEveryAsync(Pick(addresses), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Somebody else has named a peer on this torrent.
    /// </summary>
    /// <remarks>
    /// For whoever hears of one outside this run — the local network, which no
    /// tracker and no DHT can see. Dialled the same way as any other: the same
    /// address twice is one peer, whoever named it.
    /// </remarks>
    public void Met(IReadOnlyList<PeerAddress> addresses)
    {
        if (_disposed || addresses.Count == 0)
        {
            return;
        }

        _ = MeetAsync(addresses, _stopping.Token);
    }

    /// <summary>Asks the DHT who else is on this torrent.</summary>
    /// <remarks>
    /// Not awaited by the announce that starts it: a walk towards a hash takes
    /// rounds of asking and the announce loop has its own interval to keep.
    /// Whoever it finds is dialled the moment it finds them.
    /// </remarks>
    private async Task FromTheDhtAsync(TorrentMetadata torrent, CancellationToken ct)
    {
        if (_dht is null)
        {
            return;
        }

        try
        {
            PeerSearch found = await _dht.PeersAsync(torrent, DhtPeersWanted, ct).ConfigureAwait(false);

            if (found.Peers.Count > 0)
            {
                await MeetAsync(found.Peers, ct).ConfigureAwait(false);
            }
        }
        catch (Exception quiet) when (quiet is not OperationCanceledException)
        {
            // The DHT is a best effort by design: a search that fails leaves
            // the trackers and the peer exchanges exactly as they were.
        }
    }

    /// <summary>Opens one conversation, or answers nothing.</summary>
    /// <remarks>
    /// A peer that will not talk is the ordinary case rather than a fault: most
    /// of the addresses a tracker hands out are stale.
    /// </remarks>
    private async Task<PeerConnection?> DialAsync(PeerAddress address, CancellationToken ct)
    {
        try
        {
            return await _dialler
                .DialAsync(address, _infoHash, _peerId, Torrent?.PieceCount ?? 0, ct)
                .ConfigureAwait(false);
        }
        catch (Exception gone) when (gone is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// Talks to one peer until it goes or the run is stopped.
    /// </summary>
    /// <remarks>
    /// The metadata first when there is none, because until it arrives there is
    /// nothing to ask anybody for a block of, and then the session. Every
    /// failure costs this peer and nothing else: there are always more, and a
    /// torrent with forty of them must not be taken down by the one that hung
    /// up mid-message.
    /// </remarks>
    private async Task ConverseAsync(PeerConnection peer, CancellationToken ct)
    {
        try
        {
            // A read this peer had already begun when the metadata arrived,
            // handed on rather than abandoned. Two readers on one connection
            // is worse than one, and cancelling a read that may already have
            // taken bytes off the socket loses the frame it was in the middle
            // of.
            Task<PeerMessage?>? pending = null;

            if (Torrent is null)
            {
                pending = await FetchAsync(peer, ct).ConfigureAwait(false);
            }

            if (Session() is TorrentSession session)
            {
                await session.RunAsync(peer, ct, pending).ConfigureAwait(false);
            }
        }
        catch (Exception gone) when (gone is not OperationCanceledException)
        {
            // One peer is one peer.
        }
        finally
        {
            lock (_lock)
            {
                _peers.Remove(peer);
                _talkingTo.Remove(peer);
            }

            peer.Dispose();
        }
    }

    /// <summary>
    /// Asks one peer for the metadata a magnet does not carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The extension handshake first, because the id every <c>ut_metadata</c>
    /// message must be sent under is whatever that peer chose and is in it. A
    /// peer that does not offer the extension is asked for nothing at all.
    /// </para>
    /// <para>
    /// It returns the moment the metadata is whole and leaves the connection
    /// open for the session: the peer that had the metadata is the one most
    /// likely to have the pieces.
    /// </para>
    /// </remarks>
    private async Task<Task<PeerMessage?>?> FetchAsync(PeerConnection peer, CancellationToken ct)
    {
        await peer.SendAsync(Extensions.Handshake(Client), ct).ConfigureAwait(false);

        int? theirs = null;

        while (Torrent is null && !ct.IsCancellationRequested)
        {
            Task<PeerMessage?> next = peer.NextAsync(ct);

            // Either this peer says something, or somebody else finishes the
            // metadata. Waiting only on the peer parked it here for the rest of
            // the run: a seed has every piece, so it sends no `have`, and it
            // will not unchoke a client that never said it was interested —
            // which this client cannot say until it is in the session. A
            // keep-alive does not wake it either, because the connection
            // swallows those.
            //
            // So only the peer that happened to deliver the last block of the
            // metadata ever reached the session, and every other one sat in
            // this read being asked for nothing until the far end dropped it
            // for being idle. On 30 August 2026 a season pack fetched its
            // metadata at 05:15 and was at nought peers by 05:29 with hundreds
            // of seeds in the swarm.
            if (await Task.WhenAny(next, _metadata.Task).ConfigureAwait(false) != (Task)next)
            {
                return next;
            }

            PeerMessage? message = await next.ConfigureAwait(false);

            if (message is null)
            {
                return null;
            }

            if (message.Id != PeerMessageId.Extended || message.Payload.Length < 1)
            {
                continue;
            }

            if (message.Payload[0] == Extensions.HandshakeId)
            {
                ExtensionHandshake handshake = Extensions.Read(message);

                if (handshake.MetadataId is not int id || handshake.MetadataSize is not int size)
                {
                    // It cannot help with this. Not a fault and not worth a
                    // line: it is still a peer, and the session will have it —
                    // which is what returning here did not do. There is no
                    // session while the torrent is unknown, so the conversation
                    // ended and the connection was disposed. Waited for
                    // instead, holding a peer this client will want the moment
                    // it knows what the torrent is.
                    await _metadata.Task.WaitAsync(ct).ConfigureAwait(false);

                    return null;
                }

                theirs = id;

                await AskAsync(peer, id, Fetch(size), ct).ConfigureAwait(false);

                continue;
            }

            if (message.Payload[0] == Extensions.OurExchangeId)
            {
                // Peers before pieces. A magnet is exactly when this client has
                // fewest of them and needs most: the metadata itself has to
                // come from somebody.
                PexUpdate offered = Pex.Read(message);

                if (offered.Added.Count > 0)
                {
                    // Not awaited: dialling twenty peers must not hold up the
                    // metadata this loop is here for.
                    _ = MeetAsync(offered.Added, ct);
                }

                continue;
            }

            if (message.Payload[0] != Extensions.OurMetadataId || theirs is not int already)
            {
                continue;
            }

            await TakeAsync(peer, already, message, ct).ConfigureAwait(false);
        }

        return null;
    }

    /// <summary>Takes one metadata message from a peer, and finishes if it was the last.</summary>
    private async Task TakeAsync(PeerConnection peer, int theirs, PeerMessage message, CancellationToken ct)
    {
        MetadataPart part = MetadataTransfer.Read(message);

        if (part.Kind != MetadataMessage.Data)
        {
            // A reject is a peer that has dropped the metadata since its
            // handshake, and a request is one asking us for what we have not
            // got yet. Neither is an answer.
            return;
        }

        MetadataFetch fetch = Fetch(part.TotalSize);

        fetch.Add(part.Piece, part.Data, peer.Introduction.Client);

        if (!fetch.Complete)
        {
            return;
        }

        if (!fetch.Verified)
        {
            // Every piece arrived and the whole does not hash to the info hash
            // this torrent is for, so at least one peer sent rubbish. Every
            // contributor is dropped and it starts again.
            fetch.Discard();

            await AskAsync(peer, theirs, fetch, ct).ConfigureAwait(false);

            return;
        }

        lock (_lock)
        {
            // The trackers come from the magnet: an info dictionary carries
            // none, and a client that took its word for it would announce to
            // nobody.
            _torrent ??= fetch.Read(_trackers);
        }

        // Written down the moment it is known, so no restart ever asks the
        // swarm for it again. A swarm that has gone quiet cannot answer, and a
        // torrent that cannot fetch its metadata is given up on however
        // complete it is on disk.
        _resume?.Remember(Convert.ToHexString(_infoHash), fetch.Info);

        _metadata.TrySetResult();
    }

    /// <summary>Asks one peer for every piece of the metadata still wanted.</summary>
    private static async Task AskAsync(PeerConnection peer, int theirs, MetadataFetch fetch, CancellationToken ct)
    {
        foreach (int piece in fetch.Wanted().ToArray())
        {
            await peer.SendAsync(MetadataTransfer.Request(theirs, piece), ct).ConfigureAwait(false);
        }
    }

    /// <summary>The one fetch for this torrent, made the first time a peer says how big it is.</summary>
    private MetadataFetch Fetch(int size)
    {
        lock (_lock)
        {
            // The first size any peer gave. Two peers that disagree cannot both
            // be right, and the hash at the end is what settles it.
            return _fetch ??= new(_infoHash, size, _time.GetUtcNow());
        }
    }

    /// <summary>What this client calls itself to a peer.</summary>
    private const string Client = "NoMercy Torrent Downloader";

    /// <summary>
    /// The session, opened the first time there is metadata to open it with.
    /// </summary>
    /// <remarks>
    /// Not before: the disk needs the file list and the piece hashes, and a
    /// session built from a magnet alone would have nowhere to put a block and
    /// nothing to check it against.
    /// </remarks>
    private TorrentSession? Session()
    {
        while (true)
        {
            TorrentMetadata torrent;
            IReadOnlyList<TorrentFileEntry> keeping;

            lock (_lock)
            {
                if (_session is not null)
                {
                    return _session;
                }

                if (_torrent is null)
                {
                    return null;
                }

                if (_verifying)
                {
                    // Somebody else is reading the disk. Waited for below with
                    // the lock let go, so a caller that really needs the
                    // session still gets it and everything that only wants to
                    // ask this run something is answered meanwhile.
                    goto waiting;
                }

                keeping = _choose is null ? _torrent.Files : _choose(_torrent.Files);

                if (keeping.Count == 0)
                {
                    _nothingWanted = true;

                    // Nothing in it is worth a byte. Said rather than started:
                    // the caller stops this torrent and blames it, and creating
                    // a session that wants no pieces would report itself
                    // finished the moment it existed.
                    return null;
                }

                torrent = _torrent;
                _verifying = true;

                _opened.Reset();
            }

            try
            {
                // **Outside the lock, and that is the whole point.** With no
                // resume file to go by this reads and SHA-1s every piece on
                // disk: minutes for a season pack. It used to run inside the
                // lock, and everything that asks this run anything takes that
                // same lock — including Progress, which the engine's StatusAsync
                // calls for every torrent while holding its own lock, which is
                // what the Downloads page is rendered from, in the media
                // server's own request thread.
                //
                // So opening a 37 GB torrent stopped the plugin's pages
                // answering at all, the owner's dashboard dropped its
                // connection and picked it up again, and the whole server
                // looked hung. On every restart, because a torrent with no
                // resume file is hashed again every time.
                TorrentDisk disk = new(torrent, _folder);

                // **Verified before Create, and the order is the whole point.**
                // Create sets every file to its full length, so a verification
                // that runs after it asks "is there anything on disk?" about
                // files it has just made itself — and is answered yes. A fresh
                // forty-five gigabyte season pack then had every piece of its
                // own empty, sparse files read and SHA-1'd before it could ask
                // a single peer for a byte, which is what the owner watched sit
                // at "fetching metadata" on 2 September 2026.
                //
                // Nothing is lost by asking first. A torrent with files already
                // on disk is exactly the one this is for, and they are there to
                // be read; a torrent with none answers in no time and Create
                // then makes them.
                Bitfield have = (_verify ?? Verified)(torrent, disk);

                disk.Create();

                lock (_lock)
                {
                    _disk = disk;

                    _session ??= new(
                        torrent,
                        disk,
                        have,
                        keeping.Count == torrent.Files.Count ? null : torrent.PiecesOf(keeping),
                        time: _time,
                        limits: _limits,

                        // What the session is told about, this run dials. The
                        // session owns no sockets on purpose.
                        met: addresses => _ = MeetAsync(addresses, _stopping.Token));

                    return _session;
                }
            }
            finally
            {
                lock (_lock)
                {
                    _verifying = false;
                }

                _opened.Set();
            }

        waiting:

            // Never for ever: a pass that faulted sets this on its way out, and
            // this is a fallback for a thread that should never be left here.
            _opened.Wait(Opening);
        }
    }

    /// <summary>The longest a caller waits on somebody else opening the session.</summary>
    /// <remarks>
    /// Long, because it is a read of every byte on disk and a big torrent takes
    /// its time. It is a backstop and not a schedule: whoever is opening sets
    /// this the moment it is done, faulted or not.
    /// </remarks>
    private static readonly TimeSpan Opening = TimeSpan.FromMinutes(30);

    /// <summary>
    /// What is on disk, hashed, when there is no resume file to go by.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bytes are the truth. A torrent with no resume file used to be taken
    /// to have nothing at all — so a download already finished on disk went
    /// back to nought on the next restart and fetched every byte again. On
    /// 23 August 2026 twenty-three finished episodes did exactly that, and none
    /// of them could ever be staged because none of them was ever complete
    /// again.
    /// </para>
    /// <para>
    /// It costs one pass over the files, once, on a torrent that has any of
    /// them. A folder with nothing in it is not read at all: a fresh torrent
    /// pays nothing for this.
    /// </para>
    /// </remarks>
    private static Bitfield Hashed(TorrentMetadata torrent, TorrentDisk disk)
    {
        Bitfield have = new(torrent.PieceCount);

        if (!torrent.Files.Any(file => File.Exists(disk.PathOf(file)) && new FileInfo(disk.PathOf(file)).Length > 0))
        {
            return have;
        }

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            try
            {
                byte[] bytes = disk.Read(piece * torrent.PieceLength, (int)torrent.LengthOfPiece(piece));

                if (SHA1.HashData(bytes).AsSpan().SequenceEqual(torrent.Pieces[piece]))
                {
                    have.Set(piece);
                }
            }
            catch (IOException)
            {
                // A file shorter than the torrent says, or one that cannot be
                // read. That piece is simply not here, which is what an empty
                // bit means.
            }
        }

        return have;
    }

    /// <summary>
    /// What this torrent would write down to be picked up again.
    /// </summary>
    /// <remarks>
    /// Null until there is something to say: a torrent with no metadata has no
    /// piece count and a torrent with no session has verified nothing.
    /// </remarks>
    public ResumeData? Resuming()
    {
        lock (_lock)
        {
            if (_session is null || _torrent is null || _disk is null)
            {
                return null;
            }

            SessionProgress progress = _session.Progress();

            return new(
                _torrent.InfoHash,
                _session.Verified,
                progress.Uploaded,
                progress.Downloaded,
                [
                    .. _torrent.Files
                        .Select(file => new FileInfo(_disk.PathOf(file)))
                        .Zip(_torrent.Files, (FileInfo now, TorrentFileEntry file) =>
                            new ResumeFile(file.Path, now.Exists ? now.Length : 0, now.Exists ? now.LastWriteTimeUtc : default)),
                ]);
        }
    }

    /// <summary>
    /// What is already on disk and can be believed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The resume file, judged against the files as they are now. Without it
    /// every restart verifies the whole torrent again, which for a
    /// six-gigabyte file on a spinning disk is minutes of the server doing
    /// nothing else — and it is what 0.3.4 did on every start.
    /// </para>
    /// <para>
    /// It is a cache and is treated as one: a file whose size or modification
    /// time has changed takes every piece covering it back to unverified,
    /// because the bytes are the truth and the file is only a claim about them.
    /// </para>
    /// </remarks>
    private Bitfield Verified(TorrentMetadata torrent, TorrentDisk disk)
    {
        if (_resume?.Load(torrent.InfoHash) is not ResumeData stored)
        {
            return Hashed(torrent, disk);
        }

        Dictionary<string, ResumeFile> onDisk = new(StringComparer.Ordinal);

        foreach (TorrentFileEntry file in torrent.Files)
        {
            FileInfo now = new(disk.PathOf(file));

            if (now.Exists)
            {
                onDisk[file.Path] = new(file.Path, now.Length, now.LastWriteTimeUtc);
            }
        }

        return stored.Trust(torrent, onDisk);
    }

    /// <summary>
    /// What to write down about this torrent, or nothing while there is nothing
    /// worth writing.
    /// </summary>
    /// <remarks>
    /// A torrent with no metadata has no piece count and nothing verified, and
    /// a resume file for it would be a claim about a torrent nobody can
    /// describe.
    /// </remarks>
    public ResumeData? ResumePoint()
    {
        lock (_lock)
        {
            if (_session is null || _torrent is null || _disk is null)
            {
                return null;
            }

            SessionProgress progress = _session.Progress();

            return new(
                _torrent.InfoHash,
                _session.Verified,
                progress.Uploaded,
                progress.Downloaded,
                [
                    .. _torrent.Files
                        .Select(file => new FileInfo(_disk.PathOf(file)))
                        .Where(one => one.Exists)
                        .Zip(_torrent.Files, (one, file) => new ResumeFile(file.Path, one.Length, one.LastWriteTimeUtc)),
                ]);
        }
    }
}
