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
/// The protocol itself lives in the Bittorrent assembly, which references
/// nothing; this is the adapter that gives it the shape <c>Core</c> asks for.
/// It is what the port and the lifetime hang off, and the pieces that do the
/// downloading arrive behind it slice by slice.
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
    TimeProvider? time = null)
    : ITorrentEngine, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Transfer> _torrents = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;
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

            if (!_torrents.TryGetValue(magnet.InfoHash, out Transfer? already))
            {
                _torrents[magnet.InfoHash] = new(
                    magnet.InfoHash,
                    magnet.DisplayName,
                    // Every tracker anybody named for it, the magnet's own and
                    // the ones the find stage merged, without duplicates.
                    [.. magnet.Trackers.Union(request.Trackers, StringComparer.OrdinalIgnoreCase)],
                    request.DownloadFolder,
                    request.ExpectedBytes,
                    _time.GetUtcNow());
            }
            else
            {
                // The same torrent from a second source is one torrent with
                // more trackers, which is the whole reason every indexer is
                // asked.
                already.Add(request.Trackers.Union(magnet.Trackers, StringComparer.OrdinalIgnoreCase));
            }

            return Task.FromResult(new TorrentHandle(magnet.InfoHash, _torrents[magnet.InfoHash].Name));
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
            return _torrents.TryGetValue(infoHash, out Transfer? transfer) ? transfer.Trackers : [];
        }
    }

    public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
    {
        lock (_lock)
        {
            DateTimeOffset now = _time.GetUtcNow();

            foreach (Transfer transfer in _torrents.Values)
            {
                Expire(transfer, now);
            }

            return Task.FromResult<IReadOnlyList<TorrentStatus>>([.. _torrents.Values.Select(one => one.Status())]);
        }
    }

    public Task PauseAsync(string infoHash, CancellationToken ct)
    {
        // Only the state. Everything else the transfer knows — what is verified
        // above all — is what makes resuming cost nothing.
        return Set(infoHash, TorrentState.Paused);
    }

    /// <summary>
    /// What this torrent has verified on disk, or null while nothing knows.
    /// </summary>
    /// <remarks>
    /// Not on the port: the pipeline has no use for a bitfield and would only
    /// be able to misread it. It is here so that a pause, a resume and a
    /// restart can all be seen to keep it.
    /// </remarks>
    public Bitfield? VerifiedPieces(string infoHash)
    {
        lock (_lock)
        {
            return _torrents.TryGetValue(infoHash, out Transfer? transfer) ? transfer.Verified : null;
        }
    }

    /// <summary>Notes what verification found, so that a pause does not lose it.</summary>
    public void Verified(string infoHash, Bitfield verified)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Transfer? transfer))
            {
                transfer.Verified = verified;
            }
        }
    }

    public Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Transfer? transfer))
            {
                // Back to waiting for its metadata, which is where every
                // torrent here still is: nothing fetches it until the slice
                // that does arrives, and saying "downloading" would be saying
                // something untrue. The clock restarts with it, or a torrent
                // resumed after the limit had passed would fail on the tick
                // that followed without anybody having been asked again.
                transfer.Restart(_time.GetUtcNow());
            }

            return Task.CompletedTask;
        }
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        lock (_lock)
        {
            _torrents.Remove(infoHash);
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
                _torrents.TryGetValue(infoHash, out Transfer? transfer) ? transfer.Files : []);
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

            _sockets?.Dispose();
            _sockets = null;
            _torrents.Clear();
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
    /// Said once, not once a tick: transfers ticks every minute, and the state
    /// it moves to is the thing that stops it being said again.
    /// </para>
    /// </remarks>
    private void Expire(Transfer transfer, DateTimeOffset now)
    {
        if (transfer.State != TorrentState.FetchingMetadata || now - transfer.Since < metadataTimeout)
        {
            return;
        }

        // Its own words, and they name the limit so the owner knows which
        // setting to change.
        transfer.Fail($"No peer sent its metadata within {metadataTimeout.TotalMinutes:0.#} minutes.");

        logger.LogWarning("{Hash} was dropped: {Reason}", transfer.InfoHash, transfer.Error);
        journal.Failed(ActivityStage.Download, transfer.Name ?? transfer.InfoHash, transfer.Error!);
    }

    private Task Set(string infoHash, TorrentState state)
    {
        lock (_lock)
        {
            if (_torrents.TryGetValue(infoHash, out Transfer? transfer))
            {
                transfer.State = state;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>One torrent the client is holding.</summary>
    private sealed class Transfer(
        string infoHash,
        string? name,
        IEnumerable<string> trackers,
        string folder,
        long? expectedBytes,
        DateTimeOffset since)
    {
        private readonly List<string> _trackers = [.. trackers];

        public string InfoHash { get; } = infoHash;

        public string? Name { get; } = name;

        /// <summary>When its metadata was last asked for.</summary>
        public DateTimeOffset Since { get; private set; } = since;

        /// <summary>
        /// Which pieces are verified on disk, or null before anything is known.
        /// </summary>
        /// <remarks>
        /// It survives a pause. A client that threw the bitfield away when the
        /// owner pressed pause would verify the whole torrent again on resume,
        /// which for a six-gigabyte file is minutes of the server doing nothing
        /// else — and would look, from the page, exactly like starting over.
        /// </remarks>
        public Bitfield? Verified { get; set; }

        /// <summary>What went wrong, in its own words, or null.</summary>
        public string? Error { get; private set; }

        public IReadOnlyList<string> Trackers => _trackers;

        public string Folder { get; } = folder;

        public IReadOnlyList<TorrentFile> Files { get; } = [];

        public TorrentState State { get; set; } = TorrentState.FetchingMetadata;

        /// <summary>Back to fetching its metadata, with the clock started again.</summary>
        public void Restart(DateTimeOffset now)
        {
            State = TorrentState.FetchingMetadata;
            Error = null;
            Since = now;
        }

        /// <summary>Puts it in the error state with the reason it got there.</summary>
        public void Fail(string reason)
        {
            State = TorrentState.Error;
            Error = reason;
        }

        public void Add(IEnumerable<string> more)
        {
            foreach (string tracker in more)
            {
                if (!_trackers.Contains(tracker, StringComparer.OrdinalIgnoreCase))
                {
                    _trackers.Add(tracker);
                }
            }
        }

        public TorrentStatus Status()
        {
            _ = Folder;

            return new(
                InfoHash,
                Name,
                State,
                // Nothing has been downloaded, and every number says so rather
                // than being drawn as something. The size is what the indexer
                // claimed, which is not the same as what the metadata will say.
                BytesDone: 0,
                BytesTotal: expectedBytes,
                DownloadRateBytesPerSecond: 0,
                UploadRateBytesPerSecond: 0,
                Peers: 0,
                Seeds: 0,
                // Null, not nought: nothing has been downloaded, so there is no
                // ratio to have.
                Ratio: null,
                Eta: null,
                Error);
        }
    }
}
