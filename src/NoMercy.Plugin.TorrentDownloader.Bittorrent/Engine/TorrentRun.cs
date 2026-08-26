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
    /// Every peer this run has dialled, whether it answered or not.
    /// </summary>
    /// <remarks>
    /// A peer already tried is one the next announce names again. Re-dialling
    /// it every interval is a connection a minute to the same machine, which is
    /// how a client gets itself banned by a swarm it is trying to join.
    /// </remarks>
    private readonly HashSet<string> _tried = new(StringComparer.Ordinal);

    private readonly List<PeerConnection> _peers = [];
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
    private bool _nothingWanted;

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
        RateLimits? limits = null)
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
            lock (_lock)
            {
                // Asked for rather than waited for: nothing decides which files
                // are wanted until something asks for the session.
                Session();

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
    /// When to announce again, as the trackers themselves asked.
    /// </summary>
    /// <remarks>
    /// docs/06-torrent-client.md: announce at the tracker's own interval. The
    /// shortest interval anybody asked for, raised to the longest floor anybody
    /// set — a floor is what earns a ban when it is breached, so it wins over
    /// another tracker's wish to be asked sooner.
    /// </remarks>
    public TimeSpan Interval { get; private set; } = DefaultInterval;

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

        IReadOnlyList<TrackerResult> answers = await _trackerSet
            .AnnounceAsync(_trackers, Request(), ct)
            .ConfigureAwait(false);

        Told(answers);

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

        // The dials are awaited and the conversations are not. A dial has an
        // end — the peer answers or it does not — and a conversation lasts as
        // long as the peer does, so an announce pass that waited for one would
        // never come round again.
        foreach (PeerConnection? peer in await Task.WhenAll(fresh.Select(one => DialAsync(one, ct))))
        {
            if (peer is null)
            {
                continue;
            }

            lock (_lock)
            {
                _peers.Add(peer);
                _conversations.Add(ConverseAsync(peer, ct));
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
            _conversations.Add(ConverseAsync(peer, ct));
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

    /// <summary>What this client tells a tracker about itself.</summary>
    /// <summary>
    /// What is announced as still wanted before the metadata says otherwise.
    /// </summary>
    /// <remarks>
    /// Any non-zero number gets peers; this one is a terabyte so that no
    /// tracker ranking by need reads a client that knows nothing as one that
    /// is nearly finished.
    /// </remarks>
    private const long UnknownSize = 1L << 40;

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
            if (Torrent is null)
            {
                await FetchAsync(peer, ct).ConfigureAwait(false);
            }

            if (Session() is TorrentSession session)
            {
                await session.RunAsync(peer, ct).ConfigureAwait(false);
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
    private async Task FetchAsync(PeerConnection peer, CancellationToken ct)
    {
        await peer.SendAsync(Extensions.Handshake(Client), ct).ConfigureAwait(false);

        int? theirs = null;

        while (Torrent is null && !ct.IsCancellationRequested)
        {
            PeerMessage? message = await peer.NextAsync(ct).ConfigureAwait(false);

            if (message is null)
            {
                return;
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
                    // line: it is still a peer, and the session will have it.
                    return;
                }

                theirs = id;

                await AskAsync(peer, id, Fetch(size), ct).ConfigureAwait(false);

                continue;
            }

            if (message.Payload[0] != Extensions.OurMetadataId || theirs is not int already)
            {
                continue;
            }

            await TakeAsync(peer, already, message, ct).ConfigureAwait(false);
        }
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

            IReadOnlyList<TorrentFileEntry> keeping = _choose is null ? _torrent.Files : _choose(_torrent.Files);

            if (keeping.Count == 0)
            {
                _nothingWanted = true;

                // Nothing in it is worth a byte. Said rather than started: the
                // caller stops this torrent and blames it, and creating a
                // session that wants no pieces would report itself finished
                // the moment it existed.
                return null;
            }

            _disk = new(_torrent, _folder);
            _disk.Create();

            _session = new(
                _torrent,
                _disk,
                Verified(_torrent, _disk),
                keeping.Count == _torrent.Files.Count ? null : _torrent.PiecesOf(keeping),
                time: _time,
                limits: _limits);

            return _session;
        }
    }

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
