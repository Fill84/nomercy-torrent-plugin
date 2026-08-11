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
    private readonly ConcurrentDictionary<string, PausedTorrent> _paused = new();

    /// <summary>Magnets whose swarm has not answered yet. See <see cref="ResolvingTorrent"/>.</summary>
    private readonly ConcurrentDictionary<string, ResolvingTorrent> _resolving = new();

    /// <summary>
    /// Cancelled by Dispose, so a background resolution cannot outlive the engine.
    ///
    /// <para>
    /// Load-bearing on Windows: a task still running holds the plugin's collectible load
    /// context alive, which keeps its files locked and makes the plugin impossible to
    /// update without stopping the server.
    /// </para>
    /// </summary>
    private readonly CancellationTokenSource _lifetime = new();
    private readonly byte[] _peerId = Handshake.NewPeerId();
    private bool _disposed;

    private MagnetResolver Resolver => new(trackers, dialer, _peerId);

    public async Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // A .torrent URL is a fetch and a parse: quick, and it fails in a way the caller can
        // act on straight away. A magnet is a conversation with a swarm that may not exist,
        // so it must not be had on the caller's thread. This used to await BEP 9 for five
        // minutes and then throw, which took the caller's whole cycle with it - and threw
        // before the grab was ever recorded, so nothing anywhere remembered the attempt.
        if (!request.Source.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
            return await StartAsync(await ResolveAsync(request, ct), request, ct);

        MagnetLink magnet = MagnetLink.Parse(request.Source);
        string infoHash = Convert.ToHexStringLower(magnet.InfoHash);

        // Adding the same torrent twice is not an error - two episodes can want one
        // season pack, and a retry can arrive while the first attempt is still running.
        if (_torrents.ContainsKey(infoHash) || _resolving.ContainsKey(infoHash))
            return infoHash;

        _resolving[infoHash] = new ResolvingTorrent
        {
            InfoHash = infoHash,
            Request = request,
            StartedAt = now(),
        };

        ResolveInBackground(infoHash, request);

        return infoHash;
    }

    /// <summary>
    /// Asks the swarm what the torrent contains, and starts it when the answer arrives.
    ///
    /// <para>
    /// Fire and forget on purpose, and the only place in this engine that is. Nothing awaits
    /// it because the point is that the caller does not: the transfer list is how anybody
    /// learns how it went, which is how they learn about every other change of state here.
    /// </para>
    /// </summary>
    private void ResolveInBackground(string infoHash, TorrentRequest request)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                TorrentMetadata metadata = await ResolveAsync(request, _lifetime.Token);

                await StartAsync(metadata, request, _lifetime.Token);

                _resolving.TryRemove(infoHash, out _);
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
                // The engine is going away. Not this torrent's failure, and not worth
                // reporting as one.
                _resolving.TryRemove(infoHash, out _);
            }
            catch (Exception failure)
            {
                // Recorded on the torrent rather than thrown into nothing. A background
                // task's exception has nowhere to go, and the whole reason this moved off
                // the caller's thread is that throwing there destroyed the caller's cycle.
                if (_resolving.TryGetValue(infoHash, out ResolvingTorrent? waiting))
                    waiting.FailureReason = Explain(failure);
            }
        });
    }

    /// <summary>
    /// What to tell the owner, out of what the exception happens to be.
    ///
    /// <para>
    /// A metadata timeout arrives as a cancellation rather than a
    /// <see cref="MetadataException"/>, because that is how the resolver enforces the
    /// deadline - so on a real server every dead swarm was reported as "The operation was
    /// canceled", which reads as the plugin having given up on itself rather than as
    /// nobody answering. Any cancellation reaching here that is not this engine shutting
    /// down is that deadline expiring; shutdown is caught above.
    /// </para>
    /// </summary>
    private static string Explain(Exception failure) => failure switch
    {
        // Its own message, and it is the one worth reading: it names the file that got the
        // torrent refused.
        TorrentContentException refused => refused.Message,

        MetadataException or OperationCanceledException or TimeoutException =>
            "no peer offered this torrent's contents within the time allowed - the swarm may have nobody in it",
        _ => failure.Message,
    };


    /// <summary>Everything after the metadata is known, whichever of the two ways it was learned.</summary>
    private async Task<string> StartAsync(TorrentMetadata metadata, TorrentRequest request, CancellationToken ct)
    {
        string infoHash = Convert.ToHexStringLower(metadata.InfoHash);

        if (_torrents.ContainsKey(infoHash))
            return infoHash;

        // Before a single piece is asked for. A release named like an episode turned out on
        // a real server to be one 1.2 GB .scr - a Windows executable padded out to look like
        // video - and the engine wrote it to disk and marked it executable, because nothing
        // between the release name and the file system ever looked at the file list. The
        // import refused it afterwards, which is the wrong place to find out: by then a
        // gigabyte of somebody else's program is on the owner's machine.
        if (TorrentContents.Refuse(metadata) is string refusal)
            throw new TorrentContentException(refusal);

        Directory.CreateDirectory(options.StateFolder);

        FilePieceStore store = new(metadata, options.DownloadFolder);
        FileResumeStore resume = new(options.StateFolder);

        Bitfield have = await resume.LoadAsync(metadata, ct) ?? new Bitfield(metadata.PieceCount);

        // The engine's policy, with the originating tracker's own targets over the top. A
        // private tracker sets its own ratio; the defaults underneath are what a torrent
        // with no tracker of its own gets, and it will never upload anyway.
        SwarmPolicy policy = options.Policy with
        {
            SeedRatioTarget = request.SeedRatioTarget ?? options.Policy.SeedRatioTarget,
            SeedTimeTarget = request.SeedTimeTarget ?? options.Policy.SeedTimeTarget,
        };

        TorrentSession session = new(
            metadata,
            store,
            resume,
            have,
            policy,
            PieceServer.For(metadata, store, policy, request.Origin, have, metadata.TotalLength));

        RunningTorrent running = new(infoHash, metadata, session, store, request, now());
        _torrents[infoHash] = running;

        running.Start(this, ct);

        return infoHash;
    }

    public async Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        // A paused torrent is still one the owner can throw away, and it is not in
        // _torrents. Without this, removing one would report success and change nothing.
        _paused.TryRemove(infoHash, out _);

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

    /// <summary>
    /// Stops the torrent and keeps its request, rather than inventing a suspended state.
    ///
    /// <para>
    /// A paused torrent is exactly a torrent this process is not currently running, and
    /// the engine already knows how to start one of those from pieces on disk - it is what
    /// it does after every server restart. Resuming down that same path means pause has no
    /// recovery logic of its own, and therefore no second way for recovery to be wrong.
    /// </para>
    /// </summary>
    public async Task PauseAsync(string infoHash, CancellationToken ct)
    {
        if (!_torrents.TryRemove(infoHash, out RunningTorrent? running))
            return;

        _paused[infoHash] = new PausedTorrent(
            running.Request,
            running.Metadata.TotalLength,
            Math.Min((long)running.Session.Have.SetCount * running.Metadata.PieceLength, running.Metadata.TotalLength));

        await running.StopAsync();
    }

    public async Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        if (!_paused.TryRemove(infoHash, out PausedTorrent? paused))
            return;

        try
        {
            await AddAsync(paused.Request, ct);
        }
        catch
        {
            // Put it back rather than losing it. A resume that cannot reach the tracker
            // right now is a resume to try again in a minute, not a download the owner
            // silently no longer has.
            _paused[infoHash] = paused;
            throw;
        }
    }

    public Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<EngineTransfer>>(
        [
            .. _torrents.Values.Select(Describe),

            // Listed with no bytes and no total, because until a peer answers there is
            // nothing to be a fraction of. A magnet that gave up stays in this list rather
            // than vanishing: the reason is the only thing anybody can act on, and a
            // torrent that disappears silently is what this whole state was added to end.
            .. _resolving.Values.Select(waiting => new EngineTransfer
            {
                InfoHash = waiting.InfoHash,
                State = waiting.FailureReason is null ? EngineState.Resolving : EngineState.Failed,
                FailureReason = waiting.FailureReason,
            }),
            .. _paused.Select(entry => new EngineTransfer
            {
                InfoHash = entry.Key,
                State = EngineState.Paused,
                BytesDone = entry.Value.BytesDone,
                BytesTotal = entry.Value.BytesTotal,
            }),
        ]);

    /// <summary>What is kept about a torrent that is not running: enough to start it again and to draw a bar.</summary>
    private sealed record PausedTorrent(TorrentRequest Request, long BytesTotal, long BytesDone);

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
            BytesPerSecond = running.RateAt(done, now()),
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

        // First, so a resolution in flight stops asking rather than finishing into an
        // engine that is going away - and so its task cannot hold this plugin's load
        // context alive, which on Windows leaves the plugin's own files locked.
        await _lifetime.CancelAsync();
        _lifetime.Dispose();
        _resolving.Clear();

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
        private long _sampledBytes;
        private long _rate;
        private DateTimeOffset _sampledAt;

        public string InfoHash => infoHash;
        public TorrentMetadata Metadata => metadata;
        public TorrentSession Session => session;
        public TorrentRequest Request => request;
        public DateTimeOffset StartedAt => startedAt;
        public bool Announced { get; private set; }
        public int PeerCount => Volatile.Read(ref _peers);

        public void PeerConnected() => Interlocked.Increment(ref _peers);

        /// <summary>
        /// The rate since the last sample, kept between samples.
        ///
        /// <para>
        /// Two seconds apart at least, because the page polls faster than that and a
        /// window narrower than a piece reads as zero on a torrent that is downloading
        /// perfectly well. The previous answer is repeated in between rather than a fresh
        /// zero, which is the difference between a number and a flicker.
        /// </para>
        /// </summary>
        public long RateAt(long done, DateTimeOffset at)
        {
            if (_sampledAt == default)
            {
                _sampledAt = at;
                _sampledBytes = done;

                return 0;
            }

            double seconds = (at - _sampledAt).TotalSeconds;

            if (seconds < 2)
                return Volatile.Read(ref _rate);

            long rate = (long)Math.Max(0, (done - _sampledBytes) / seconds);

            Volatile.Write(ref _rate, rate);
            _sampledAt = at;
            _sampledBytes = done;

            return rate;
        }

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
