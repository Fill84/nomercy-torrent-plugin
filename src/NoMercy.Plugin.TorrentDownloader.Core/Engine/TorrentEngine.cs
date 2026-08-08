// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Collections.Concurrent;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

/// <summary>Fetches the bytes of a <c>.torrent</c> named by a URL. Behind an interface so a test needs no web server.</summary>
public interface ITorrentFileFetcher
{
    Task<byte[]> FetchAsync(string url, CancellationToken ct);
}

public sealed record TorrentEngineOptions
{
    public required string DownloadFolder { get; init; }

    /// <summary>Where the resume records live. Separate from the media so a cleanup of one never takes the other.</summary>
    public required string StateFolder { get; init; }

    public SwarmPolicy Policy { get; init; } = SwarmPolicy.Default;

    /// <summary>How long to keep trying before a torrent with no peers is called dead.</summary>
    public TimeSpan NoPeersTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>How long to spend asking peers for a magnet's metadata. A swarm with peers answers in seconds.</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromMinutes(5);
}

/// <summary>
/// Several torrents at once, each with its own session, its own peers and its own
/// place on disk.
///
/// <para>
/// This is the assembly point: metadata, discovery, dialling and a session per
/// torrent. It holds no protocol knowledge of its own - every hard question was
/// answered a layer down, and what is left is bookkeeping and a supply of peers.
/// </para>
/// </summary>
public sealed class TorrentEngine(
    IReadOnlyList<IPeerSource> trackers,
    IPeerDialer dialer,
    ITorrentFileFetcher fetcher,
    TorrentEngineOptions options,
    Func<DateTimeOffset> now) : ITorrentEngine, IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, RunningTorrent> _torrents = new();
    private readonly byte[] _peerId = Handshake.NewPeerId();
    private bool _disposed;

    private MagnetResolver Resolver => new(trackers, dialer, _peerId);

    public async Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        TorrentMetadata metadata = await ResolveAsync(request, ct);
        string infoHash = Convert.ToHexStringLower(metadata.InfoHash);

        // Adding the same torrent twice is not an error - two episodes can want one
        // season pack, and a retry can arrive while the first attempt is still running.
        if (_torrents.ContainsKey(infoHash))
            return infoHash;

        Directory.CreateDirectory(options.StateFolder);

        FilePieceStore store = new(metadata, options.DownloadFolder);
        FileResumeStore resume = new(options.StateFolder);

        Bitfield have = await resume.LoadAsync(metadata, ct) ?? new Bitfield(metadata.PieceCount);

        TorrentSession session = new(metadata, store, resume, have, options.Policy);

        RunningTorrent running = new(infoHash, metadata, session, store, request, now());
        _torrents[infoHash] = running;

        running.Start(this, ct);

        return infoHash;
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        if (!_torrents.TryRemove(infoHash, out RunningTorrent? running))
            return;

        await running.StopAsync();

        if (!deleteFiles)
            return;

        // Only what this torrent wrote. Deleting the whole download folder because one
        // torrent failed is how a plugin takes somebody's library with it.
        foreach (FileEntry file in running.Metadata.Files)
        {
            string path = Path.Combine([options.DownloadFolder, .. file.Path]);

            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch (IOException)
            {
                // A file still held open is not worth failing a removal over.
            }
        }
    }

    public Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngineTransfer>>([.. _torrents.Values.Select(Describe)]);

    private EngineTransfer Describe(RunningTorrent running)
    {
        long done = (long)running.Session.Have.SetCount * running.Metadata.PieceLength;

        if (running.Session.IsComplete)
        {
            return new EngineTransfer
            {
                InfoHash = running.InfoHash,
                State = EngineState.Completed,
                BytesDone = running.Metadata.TotalLength,
                BytesTotal = running.Metadata.TotalLength,
                Peers = running.PeerCount,
                CompletedFolder = Path.Combine(options.DownloadFolder, running.Metadata.Name),
            };
        }

        // Nothing at all after a generous wait means the swarm is not there. Saying so
        // lets the orchestrator try a different release instead of waiting forever.
        bool dead = running.PeerCount == 0
            && running.Session.Have.SetCount == 0
            && now() - running.StartedAt > options.NoPeersTimeout;

        return new EngineTransfer
        {
            InfoHash = running.InfoHash,
            State = dead ? EngineState.Failed : EngineState.Downloading,
            BytesDone = Math.Min(done, running.Metadata.TotalLength),
            BytesTotal = running.Metadata.TotalLength,
            Peers = running.PeerCount,
            FailureReason = dead ? $"no peers after {options.NoPeersTimeout.TotalMinutes:0} minutes" : null,
        };
    }

    private async Task<TorrentMetadata> ResolveAsync(TorrentRequest request, CancellationToken ct)
    {
        if (!request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            byte[] contents = await fetcher.FetchAsync(request.Source, ct);
            TorrentMetadata parsed = MetadataParser.FromTorrentFile(contents);

            return WithExtraTrackers(parsed, request.ExtraTrackers);
        }

        // A magnet names an info hash and nothing else, so the rest comes from the peers
        // over BEP 9 - and is refused unless it hashes to the hash the magnet named.
        MagnetLink magnet = MagnetLink.Parse(request.Source);

        return await Resolver.ResolveAsync(magnet, request.ExtraTrackers, options.MetadataTimeout, ct);
    }

    private static TorrentMetadata WithExtraTrackers(TorrentMetadata metadata, IReadOnlyList<string> extra)
    {
        if (extra.Count == 0)
            return metadata;

        List<string> merged = [.. metadata.Trackers];

        foreach (string tracker in extra)
        {
            if (!merged.Contains(tracker))
                merged.Add(tracker);
        }

        return metadata with { Trackers = merged };
    }

    /// <summary>
    /// Keeps asking every source for peers and dials what comes back, up to the policy's
    /// ceiling. Discovery is continuous rather than a single announce: peers leave, and
    /// a swarm that was thin at the start may not be an hour later.
    /// </summary>
    private async Task DiscoverAsync(RunningTorrent running, CancellationToken ct)
    {
        HashSet<PeerEndPoint> tried = [];

        while (!ct.IsCancellationRequested && !running.Session.IsComplete)
        {
            TimeSpan wait = TimeSpan.FromMinutes(15);

            foreach (string url in running.Metadata.Trackers)
            {
                IPeerSource? source = trackers.FirstOrDefault(candidate => candidate.CanAnnounceTo(url));

                if (source is null)
                    continue;

                try
                {
                    AnnounceResult result = await source.AnnounceAsync(url, Announce(running), ct);

                    if (result.Interval > TimeSpan.Zero && result.Interval < wait)
                        wait = result.Interval;

                    foreach (PeerEndPoint peer in result.Peers)
                    {
                        if (tried.Add(peer))
                            running.Dial(this, peer, ct);
                    }
                }
                catch (Exception failure) when (failure is not OperationCanceledException)
                {
                    // One tracker being down is not the torrent's problem. The others,
                    // and the peers already dialled, carry on.
                }
            }

            try
            {
                await Task.Delay(wait, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private AnnounceRequest Announce(RunningTorrent running) => new(
        running.Metadata.InfoHash,
        _peerId,
        Port: 6881,
        Downloaded: (long)running.Session.Have.SetCount * running.Metadata.PieceLength,
        Uploaded: 0,
        Left: running.Metadata.TotalLength - (long)running.Session.Have.SetCount * running.Metadata.PieceLength,
        running.Announced ? AnnounceEvent.None : AnnounceEvent.Started);

    private async Task ConnectAsync(RunningTorrent running, PeerEndPoint peer, CancellationToken ct)
    {
        try
        {
            Stream raw = await dialer.ConnectAsync(peer, ct);
            PeerConnection connection = await PeerConnection.DialAsync(raw, running.Metadata, _peerId, ct);

            running.Session.AddPeer(connection, ct);
            running.PeerConnected();
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // A peer refusing, timing out or failing the handshake is the steady state.
            // Hundreds of these happen per download and none of them are news.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (RunningTorrent running in _torrents.Values)
            await running.StopAsync();

        _torrents.Clear();
    }

    /// <summary>One torrent, and everything keeping it alive.</summary>
    private sealed class RunningTorrent(
        string infoHash,
        TorrentMetadata metadata,
        TorrentSession session,
        FilePieceStore store,
        TorrentRequest request,
        DateTimeOffset startedAt)
    {
        private readonly CancellationTokenSource _stopping = new();
        private int _peers;

        public string InfoHash => infoHash;
        public TorrentMetadata Metadata => metadata;
        public TorrentSession Session => session;
        public TorrentRequest Request => request;
        public DateTimeOffset StartedAt => startedAt;
        public bool Announced { get; private set; }
        public int PeerCount => Volatile.Read(ref _peers);

        public void PeerConnected() => Interlocked.Increment(ref _peers);

        public void Start(TorrentEngine engine, CancellationToken ct)
        {
            CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopping.Token);

            _ = session.RunAsync(linked.Token);
            _ = Task.Run(() => engine.DiscoverAsync(this, linked.Token), linked.Token);

            Announced = true;
        }

        public void Dial(TorrentEngine engine, PeerEndPoint peer, CancellationToken ct) =>
            _ = Task.Run(() => engine.ConnectAsync(this, peer, ct), ct);

        public async Task StopAsync()
        {
            await _stopping.CancelAsync();
            await session.StopAsync();
            await session.DisposeAsync();

            store.Dispose();
            _stopping.Dispose();
        }
    }
}
