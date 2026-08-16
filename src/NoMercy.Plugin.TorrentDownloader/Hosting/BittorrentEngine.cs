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
public sealed class BittorrentEngine(int listenPort, IActivityJournal journal, ILogger logger)
    : ITorrentEngine, IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Transfer> _torrents = new(StringComparer.OrdinalIgnoreCase);
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
                    request.ExpectedBytes);
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
            return Task.FromResult<IReadOnlyList<TorrentStatus>>([.. _torrents.Values.Select(one => one.Status())]);
        }
    }

    public Task PauseAsync(string infoHash, CancellationToken ct)
    {
        return Set(infoHash, TorrentState.Paused);
    }

    public Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        // Back to waiting for its metadata, which is where every torrent here
        // still is: nothing fetches it until the slice that does arrives, and
        // saying "downloading" would be saying something untrue.
        return Set(infoHash, TorrentState.FetchingMetadata);
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
        long? expectedBytes)
    {
        private readonly List<string> _trackers = [.. trackers];

        public string? Name { get; } = name;

        public IReadOnlyList<string> Trackers => _trackers;

        public string Folder { get; } = folder;

        public IReadOnlyList<TorrentFile> Files { get; } = [];

        public TorrentState State { get; set; } = TorrentState.FetchingMetadata;

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
                infoHash,
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
                Error: null);
        }
    }
}
