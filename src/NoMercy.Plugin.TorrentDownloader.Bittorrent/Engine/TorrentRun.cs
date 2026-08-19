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
/// <param name="Complete">Whether every piece is verified.</param>
public sealed record RunProgress(
    string? Name,
    bool HasMetadata,
    long BytesDone,
    long? BytesTotal,
    int Peers,
    int Seeds,
    long Downloaded,
    long Uploaded,
    bool Complete);

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
    private readonly Lock _lock = new();

    /// <summary>
    /// Every peer this run has dialled, whether it answered or not.
    /// </summary>
    /// <remarks>
    /// A peer already tried is one the next announce names again. Re-dialling
    /// it every interval is a connection a minute to the same machine, which is
    /// how a client gets itself banned by a swarm it is trying to join.
    /// </remarks>
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    private readonly List<PeerConnection> _peers = [];
    private TorrentSession? _session;
    private TorrentDisk? _disk;
    private TorrentMetadata? _torrent;
    private bool _disposed;

    public TorrentRun(
        byte[] infoHash,
        IReadOnlyList<string> trackers,
        string folder,
        TrackerSet trackerSet,
        IPeerDialler dialler,
        byte[] peerId,
        int listenPort,
        TimeProvider time,
        TorrentMetadata? torrent = null)
    {
        _infoHash = infoHash;
        _trackers = [.. trackers];
        _folder = folder;
        _trackerSet = trackerSet;
        _dialler = dialler;
        _peerId = peerId;
        _listenPort = listenPort;
        _time = time;
        _torrent = torrent;
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

    /// <summary>Whether this run is stopped and dialling nobody.</summary>
    public bool Paused { get; private set; }

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
                return new(_torrent?.Name, _torrent is not null, 0, _torrent?.TotalLength, _peers.Count, 0, 0, 0, false);
            }

            SessionProgress progress = _session.Progress();

            return new(
                _torrent.Name,
                true,
                progress.BytesDone,
                _torrent.TotalLength,
                progress.Peers,
                progress.Seeds,
                progress.Downloaded,
                progress.Uploaded,
                progress.Complete);
        }
    }

    /// <summary>Every file in it, or nothing while the metadata has not arrived.</summary>
    public IReadOnlyList<TorrentFileEntry> Files => Torrent?.Files ?? [];

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

        IReadOnlyList<TrackerResult> answers = await _trackerSet
            .AnnounceAsync(_trackers, Request(), ct)
            .ConfigureAwait(false);

        List<PeerAddress> fresh = [];

        lock (_lock)
        {
            foreach (PeerAddress peer in answers
                         .Where(one => one.Response is not null)
                         .SelectMany(one => one.Response!.Peers))
            {
                // The same peer from two trackers is one peer. Dialling it
                // twice is two connections to one client, which is how a swarm
                // of six comes to look like a swarm of twelve.
                if (_tried.Add(peer.ToString()))
                {
                    fresh.Add(peer);
                }
            }
        }

        await Task.WhenAll(fresh.Select(peer => TalkAsync(peer, ct))).ConfigureAwait(false);
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
            _session?.Dispose();
            _session = null;
            _disk = null;
        }
    }

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

            // What is left, or nought when nobody knows the size yet. A tracker
            // reads a left of nought as a seed, so this is the one number worth
            // being careful about before the metadata arrives — and a client
            // with no metadata has nothing to seed either way.
            progress.BytesTotal is long total ? Math.Max(0, total - progress.BytesDone) : 0,
            AnnounceEvent.Started);
    }

    /// <summary>
    /// Dials one peer and keeps talking to it until it goes.
    /// </summary>
    /// <remarks>
    /// Every failure costs this peer and nothing else. There are always more
    /// peers, and a torrent with forty of them must not be taken down by the
    /// one that hung up mid-message.
    /// </remarks>
    private async Task TalkAsync(PeerAddress address, CancellationToken ct)
    {
        PeerConnection? peer = null;

        try
        {
            peer = await _dialler
                .DialAsync(address, _infoHash, _peerId, Torrent?.PieceCount ?? 0, ct)
                .ConfigureAwait(false);

            if (peer is null)
            {
                return;
            }

            lock (_lock)
            {
                _peers.Add(peer);
            }

            if (Session() is TorrentSession session)
            {
                await session.RunAsync(peer, ct).ConfigureAwait(false);
            }
        }
        catch (Exception gone) when (gone is not OperationCanceledException)
        {
            // One peer is one peer. Half a swarm hangs up mid-message and the
            // torrent is worth more than any of them.
        }
        finally
        {
            if (peer is not null)
            {
                lock (_lock)
                {
                    _peers.Remove(peer);
                }

                peer.Dispose();
            }
        }
    }

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

            _disk = new(_torrent, _folder);
            _disk.Create();

            _session = new(_torrent, _disk, new Bitfield(_torrent.PieceCount));

            return _session;
        }
    }
}
