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
    IActivityJournal journal,
    ILogger logger,
    ITrackerTransport transport,
    IPeerDialler dialler,
    TimeProvider? time = null,
    ResumeKeeper? resume = null)
    : ITorrentEngine, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Held> _torrents = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly byte[] _peerId = PeerIdentity.New();
    private readonly CancellationTokenSource _stopping = new();
    private readonly RandomNumberGenerator _random = RandomNumberGenerator.Create();

    /// <summary>
    /// The owner's rate limits. One pair for the client and not one per
    /// torrent: the line is what has a speed.
    /// </summary>
    private readonly RateGate _downLimit = new(maxDownloadRate, time ?? TimeProvider.System);

    private readonly RateGate _upLimit = new(maxUploadRate, time ?? TimeProvider.System);
    private ListenSockets? _sockets;
    private bool _started;
    private bool _disposed;

    /// <summary>The port it is really listening on, or null before it starts.</summary>
    public int? Port => _sockets?.Port;

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

            TorrentRun run = new(
                Convert.FromHexString(infoHash),

                // Everything anybody named for it, without duplicates.
                [.. trackers.Union(request.Trackers, StringComparer.OrdinalIgnoreCase)],
                request.DownloadFolder,
                new TrackerSet(transport, _time),
                dialler,
                _peerId,
                _sockets?.Port ?? listenPort,
                _time,
                torrent,
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
                _downLimit,
                _upLimit);

            Held held = new(run, name, _time.GetUtcNow(), new(stallLimit, _time));

            _torrents[infoHash] = held;

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

    public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();

            foreach (Held held in _torrents.Values)
            {
                Expire(held, now);
                Stalled(held);
                Seeded(held, now);
            }

            Queue();

            return Task.FromResult<IReadOnlyList<TorrentStatus>>([.. _torrents.Select(one => Status(one.Key, one.Value))]);
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
                // limit had passed would otherwise fail on the very next tick
                // without a single peer having been asked.
                held.Since = _time.GetUtcNow();
                held.Error = null;
            }

            return Task.CompletedTask;
        }
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        Held? held;

        lock (_lock)
        {
            if (!_torrents.Remove(infoHash, out held))
            {
                return Task.CompletedTask;
            }
        }

        // Outside the lock: disposing a run waits on nothing, but the folder it
        // may be asked to delete is a disk operation and the rest of the client
        // must not stop for it.
        held.Run.Dispose();
        resume?.Forget(infoHash);

        if (deleteFiles)
        {
            Delete(held.Run.Folder(), infoHash);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
    {
        lock (_lock)
        {
            // Empty while the metadata has not arrived, never a guess from the
            // name: inventing a file list is how the wrong file gets staged.
            return Task.FromResult<IReadOnlyList<TorrentFile>>(
                _torrents.TryGetValue(infoHash, out Held? held)
                    ? [.. held.Run.Files.Select(file => new TorrentFile(file.Path, file.Length))]
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

            _torrents.Clear();
        }

        // The loops stop before the runs go, or one of them announces to a
        // tracker on behalf of a torrent that has been disposed.
        _stopping.Cancel();

        foreach (Held held in holding)
        {
            held.Run.Dispose();
        }

        _stopping.Dispose();

        lock (_lock)
        {
            _sockets?.Dispose();
            _sockets = null;
        }
    }

    /// <summary>
    /// Announces for one torrent, at the interval its trackers asked for.
    /// </summary>
    /// <remarks>
    /// docs/06-torrent-client.md: announce at the tracker's own interval. It
    /// runs for the client's life rather than for a cadence tick, because a
    /// swarm changes between ticks and a client that only asked once would be
    /// left with the peers of five minutes ago.
    /// </remarks>
    private async Task AnnouncingAsync(Held held, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await held.Run.OnceAsync(ct).ConfigureAwait(false);
            }
            catch (Exception wrong) when (wrong is not OperationCanceledException)
            {
                // One torrent is one torrent. A tracker set that threw must not
                // stop every other torrent from announcing.
                logger.LogWarning("Announcing failed: {Reason}", wrong.Message);
            }

            try
            {
                await Task.Delay(held.Run.Interval, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
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
    /// Said once, not once a tick: transfers ticks every minute, and the error
    /// it records is the thing that stops it being said again. A paused torrent
    /// is not failed for having sat there while it was stopped.
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

        held.Run.Pause();

        logger.LogWarning("{Name} was refused: {Reason}", held.Run.Torrent?.Name ?? held.Name, held.Error);
        journal.Failed(ActivityStage.Download, held.Run.Torrent?.Name ?? held.Name ?? "a torrent", held.Error);
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
            held.Error);
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
            return held.Queued ? TorrentState.Queued : TorrentState.Paused;
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

    /// <summary>Deletes what a removed torrent downloaded, when it was asked for.</summary>
    /// <remarks>
    /// Nothing here throws. Removing a torrent has already happened by the time
    /// the files are reached, and a folder that cannot be deleted must not undo
    /// it or take the caller down.
    /// </remarks>
    private void Delete(string folder, string infoHash)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception wrong) when (wrong is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("{Hash} was removed and its files could not be deleted: {Reason}", infoHash, wrong.Message);
        }
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

        /// <summary>Whether it is stopped because the client is full, not because the owner stopped it.</summary>
        public bool Queued { get; set; }

        /// <summary>When it finished downloading, which is when seeding started.</summary>
        public DateTimeOffset? Finished { get; set; }
    }
}
