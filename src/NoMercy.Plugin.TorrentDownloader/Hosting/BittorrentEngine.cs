using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
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

    public Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        Magnet? magnet = Magnet.Parse(request.Source);

        if (magnet is null)
        {
            // A .torrent address is in the port's documentation and nothing in
            // this plugin produces one: every copy the find stage chooses has a
            // hash, and a hash is a magnet. It is refused by name rather than
            // half-supported.
            throw new NotSupportedException(
                $"'{request.Source}' is not a magnet, and this client takes nothing else yet.");
        }

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_torrents.TryGetValue(magnet.InfoHash, out Held? already))
            {
                // The same torrent from a second source is one torrent with
                // more trackers, which is the whole reason every indexer is
                // asked.
                already.Run.Add(request.Trackers.Union(magnet.Trackers, StringComparer.OrdinalIgnoreCase));

                return Task.FromResult(new TorrentHandle(magnet.InfoHash, already.Run.Torrent?.Name ?? magnet.DisplayName));
            }

            TorrentRun run = new(
                Convert.FromHexString(magnet.InfoHash),

                // Everything anybody named for it, without duplicates.
                [.. magnet.Trackers.Union(request.Trackers, StringComparer.OrdinalIgnoreCase)],
                request.DownloadFolder,
                new TrackerSet(transport, _time),
                dialler,
                _peerId,
                _sockets?.Port ?? listenPort,
                _time,
                resume: resume);

            Held held = new(run, magnet.DisplayName, _time.GetUtcNow());

            _torrents[magnet.InfoHash] = held;

            // Discarded on purpose: the loop stops on the token and cannot
            // fault, because everything inside it is caught. Holding the task
            // would suggest something waits for it, and nothing does — a
            // shutdown that waited on an announce would wait on a socket.
            _ = AnnouncingAsync(held, _stopping.Token);

            return Task.FromResult(new TorrentHandle(magnet.InfoHash, magnet.DisplayName));
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
            }

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
        if (held.Error is not null
            || held.Run.Paused
            || held.Run.Torrent is not null
            || now - held.Since < metadataTimeout)
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
            return TorrentState.Paused;
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
    private sealed class Held(TorrentRun run, string? name, DateTimeOffset since)
    {
        public TorrentRun Run => run;

        /// <summary>What the magnet called it, until the metadata says better.</summary>
        public string? Name => name;

        /// <summary>When its clock started, which a resume restarts.</summary>
        public DateTimeOffset Since { get; set; } = since;

        /// <summary>Why it was given up on, in the client's own words.</summary>
        public string? Error { get; set; }
    }
}
