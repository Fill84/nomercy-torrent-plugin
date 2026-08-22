namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What one torrent is doing, from the inside.</summary>
/// <param name="Verified">How many pieces are on disk and hashed.</param>
/// <param name="Pieces">How many there are.</param>
/// <param name="BytesDone">How much of it is verified.</param>
/// <param name="Peers">How many connections are up.</param>
/// <param name="Seeds">How many of those have the lot.</param>
/// <param name="Downloaded">Bytes of pieces that have arrived, good and bad.</param>
/// <param name="Uploaded">Bytes of pieces that have gone out.</param>
/// <param name="WantedBytes">
/// How much of the torrent is being downloaded. Only the video files are, so on
/// a torrent that carries anything else this is smaller than the torrent —
/// and it is the number a percentage has to be taken against, or a download
/// that has everything it wants shows as nine tenths done for ever.
/// </param>
public sealed record SessionProgress(
    int Verified,
    int Pieces,
    long BytesDone,
    int Peers,
    int Seeds,
    long Downloaded,
    long Uploaded,
    long WantedBytes)
{
    /// <summary>Whether every wanted piece is verified.</summary>
    public bool Complete => BytesDone >= WantedBytes;
}

/// <summary>
/// One torrent, running: the parts of this client joined up.
/// </summary>
/// <remarks>
/// <para>
/// The picker says which piece, the assembly verifies it, the disk takes it,
/// the bitfield and the resume file follow, and the peers are told. Every one
/// of those was built and tested on its own in Sprint 5; this is the loop that
/// drives them, and without it none of them ever runs.
/// </para>
/// <para>
/// It owns no sockets. A connection arrives already introduced, from whatever
/// dialled or accepted it, so the same session serves a peer on this machine
/// and a peer across the world — and a test can put two of them together over a
/// pipe.
/// </para>
/// </remarks>
/// <param name="torrent">What the <c>.torrent</c> says.</param>
/// <param name="disk">Where the pieces are written and read.</param>
/// <param name="verified">What is already on disk and hashed.</param>
/// <param name="wanted">
/// Which pieces to download, or null for all of them. The owner's rule is that
/// only video files are downloaded, and the caller is the only thing that knows
/// what a video file is — the engine deals in pieces.
/// </param>
/// <param name="patience">
/// How long a piece may sit unanswered before it is offered to somebody else.
/// </param>
/// <param name="time">Where now comes from, so a test can hand in its own.</param>
public sealed class TorrentSession(
    TorrentMetadata torrent,
    TorrentDisk disk,
    Bitfield verified,
    Bitfield? wanted = null,
    TimeSpan? patience = null,
    TimeProvider? time = null) : IDisposable
{
    private readonly PiecePicker _picker = new(torrent.PieceCount, PiecePicker.DefaultEndgamePieces, wanted);
    private readonly Dictionary<int, PieceAssembly> _building = [];

    /// <summary>When each piece being built last heard anything.</summary>
    private readonly Dictionary<int, DateTimeOffset> _asked = [];
    private readonly PeerTrust _trust = new();
    private readonly List<PeerConnection> _peers = [];
    private readonly Lock _lock = new();
    private readonly Random _random = new();
    private long _downloaded;
    private long _uploaded;

    /// <summary>
    /// How long a piece may sit unanswered before it is given back.
    /// </summary>
    /// <remarks>
    /// A minute. No document gives a number, so this is the one this client
    /// uses: long enough that a peer feeding blocks slowly over a poor line is
    /// never given up on, short enough that a peer which has gone silent costs
    /// one piece of delay and not the whole download.
    /// </remarks>
    public static TimeSpan DefaultPatience { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How many pieces are asked of one peer at a time.
    /// </summary>
    /// <remarks>
    /// One piece at a time leaves a peer idle for a whole round trip between
    /// finishing one and being asked for the next, which on a link to another
    /// continent is most of the time. Four is enough to keep it busy without
    /// claiming pieces this client cannot get to.
    /// </remarks>
    public const int Pipeline = 4;

    /// <summary>The torrent this is for.</summary>
    public TorrentMetadata Torrent => torrent;

    /// <summary>What is verified on disk.</summary>
    public Bitfield Verified => verified;

    /// <summary>Whether every piece that is wanted is there.</summary>
    /// <remarks>
    /// Wanted, not every piece. A torrent holding an episode and something else
    /// is finished when the episode is, and a client that waited for the rest
    /// would never stage the file it downloaded.
    /// </remarks>
    public bool Complete => _picker.Missing(verified) == 0;

    /// <summary>How many bytes this session is downloading.</summary>
    public long WantedBytes
    {
        get
        {
            long bytes = 0;

            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                if (_picker.Wants(piece))
                {
                    bytes += torrent.LengthOfPiece(piece);
                }
            }

            return bytes;
        }
    }

    /// <summary>Where it stands, as a page would say it.</summary>
    public SessionProgress Progress()
    {
        lock (_lock)
        {
            return new(
                verified.Count,
                torrent.PieceCount,
                Bytes(),
                _peers.Count,
                _peers.Count(one => one.Seed),
                _downloaded,
                _uploaded,
                WantedBytes);
        }
    }

    /// <summary>
    /// Talks to one peer until the torrent is finished or the peer goes.
    /// </summary>
    /// <remarks>
    /// Both halves at once: it asks for what it is missing and answers what it
    /// is asked for. A client that only downloaded would be choked by every
    /// well-behaved peer in the swarm within a minute.
    /// </remarks>
    public async Task RunAsync(PeerConnection peer, CancellationToken ct)
    {
        lock (_lock)
        {
            _peers.Add(peer);
        }

        Task? beating = null;

        try
        {
            await peer.SendAsync(new(PeerMessageId.Bitfield, verified.Write()), ct).ConfigureAwait(false);

            if (torrent.Private)
            {
                // Unchoked from the start. Choking properly is a decision
                // across every peer at once and belongs to the choking round;
                // refusing everybody until it has run would leave a two-peer
                // swarm silent.
                //
                // A public torrent is never unchoked at all: the owner's rule
                // is that nothing taken from a public swarm goes back out, so
                // there is nobody to promise anything to. A peer starts choked
                // by BEP 3, so saying nothing is the whole of saying no.
                await peer.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), ct).ConfigureAwait(false);
            }

            if (!Complete)
            {
                await peer.SendAsync(PeerMessage.Of(PeerMessageId.Interested), ct).ConfigureAwait(false);
            }

            await Turn(peer, ct).ConfigureAwait(false);

            beating = BeatAsync(peer, ct);

            // Until the peer goes or the caller stops it. A session that hung
            // up on its own once it was complete would stop seeding the moment
            // it finished, which is the one thing a swarm cannot forgive.
            while (!ct.IsCancellationRequested)
            {
                PeerMessage? message = await peer.NextAsync(ct).ConfigureAwait(false);

                if (message is null)
                {
                    break;
                }

                await Handle(peer, message, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            lock (_lock)
            {
                _peers.Remove(peer);
                _picker.Left(peer.Has);
            }

            if (beating is not null)
            {
                // Waited for rather than dropped: it holds this peer, and a
                // beat still running after the connection is gone asks a
                // disposed socket for a piece.
                await beating.ConfigureAwait(false);
            }
        }
    }

    /// <summary>What to do with one message from one peer.</summary>
    private async Task Handle(PeerConnection peer, PeerMessage message, CancellationToken ct)
    {
        switch (message.Id)
        {
            case PeerMessageId.Piece:
                await Took(peer, message, ct).ConfigureAwait(false);
                break;

            case PeerMessageId.Request:
                await Serve(peer, message, ct).ConfigureAwait(false);
                break;

            default:
                // Something this client has no use for. Dropping the peer over
                // it would cost a connection for no reason.
                break;
        }

        await Turn(peer, ct).ConfigureAwait(false);
    }

    /// <summary>Asks this peer for whatever it has that is wanted.</summary>
    private async Task Turn(PeerConnection peer, CancellationToken ct)
    {
        if (Complete || peer.Choked)
        {
            return;
        }

        List<int> asking = [];

        lock (_lock)
        {
            Abandoned();

            _picker.Saw(peer.Has);

            while (asking.Count < Pipeline)
            {
                int? next = _picker.Next(verified, peer.Has, _building.Keys.ToHashSet(), _random);

                if (next is not int piece)
                {
                    break;
                }

                asking.Add(piece);

                if (_building.ContainsKey(piece))
                {
                    // The endgame, which hands the same piece to everybody. It
                    // is asked for but not claimed again, and asking a second
                    // peer for a piece already claimed is the point of it.
                    break;
                }

                _building[piece] = new(piece, torrent.LengthOfPiece(piece), torrent.Pieces[piece]);
                _asked[piece] = Now();
            }

            // Undone straight away: availability is counted once per peer, and
            // this is asked on every message that arrives.
            _picker.Left(peer.Has);
        }

        foreach (int piece in asking)
        {
            foreach (BlockRequest block in PiecePicker.Blocks(piece, torrent.LengthOfPiece(piece)))
            {
                await peer.SendAsync(PeerMessage.Request(block.Piece, block.Offset, block.Length), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Keeps asking a peer that has gone quiet without hanging up on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every request this client makes is made in answer to a message. A peer
    /// that stops sending messages therefore stops being asked for anything,
    /// and so does the peer next to it — because the pieces the quiet one was
    /// asked for stay claimed until something runs
    /// <see cref="Abandoned"/>, and nothing runs it unless a message arrives.
    /// </para>
    /// <para>
    /// So a download with peers on it can sit at nought bytes a second for
    /// ever, which is what the owner's did. This is the beat that gets it out
    /// of that: it costs one pass over a dictionary and, when there is nothing
    /// to ask for, nothing at all.
    /// </para>
    /// </remarks>
    private async Task BeatAsync(PeerConnection peer, CancellationToken ct)
    {
        TimeSpan every = Heartbeat(patience ?? DefaultPatience);

        try
        {
            while (!ct.IsCancellationRequested && !Complete)
            {
                await Task.Delay(every, (time ?? TimeProvider.System), ct).ConfigureAwait(false);
                await Turn(peer, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // The caller stopping, which is not a fault.
        }
        catch (IOException)
        {
            // The peer has gone. Its own loop notices and tidies up.
        }
        catch (ObjectDisposedException)
        {
            // The same, one layer down.
        }
    }

    /// <summary>How often a quiet peer is asked again.</summary>
    /// <remarks>
    /// Often enough that a piece given back is asked for again promptly, and
    /// never so often that it is asked before it could possibly have arrived.
    /// </remarks>
    private static TimeSpan Heartbeat(TimeSpan waited)
    {
        TimeSpan quarter = waited / 4;

        return quarter < TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : quarter;
    }

    /// <summary>
    /// Gives back every piece that was asked for and never answered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A piece is marked as on its way the moment it is requested, and the
    /// picker offers nobody a piece already on its way. Without this the mark
    /// is only ever cleared by the piece arriving whole or failing its hash —
    /// so a peer that took a request and then went quiet kept that piece for
    /// the rest of the run.
    /// </para>
    /// <para>
    /// It is not rare. With a hundred peers joining and leaving, the marked
    /// pieces pile up until every piece still missing is marked and the picker
    /// has nothing to offer anybody: peers connected, seeds available, nought
    /// bytes a second. That is exactly what the owner's server did on
    /// 22 August 2026, at 24.5% of a 3.6 GB episode with 95 seeds on it.
    /// </para>
    /// <para>
    /// What has been assembled so far goes with it. Half a piece from a peer
    /// that has gone cannot be finished by anybody else — the blocks it is
    /// missing were never asked of them — so keeping it would leave the piece
    /// marked all over again.
    /// </para>
    /// </remarks>
    private void Abandoned()
    {
        DateTimeOffset now = Now();
        TimeSpan waited = patience ?? DefaultPatience;

        foreach (int piece in _asked.Where(one => now - one.Value >= waited).Select(one => one.Key).ToArray())
        {
            _building.Remove(piece);
            _asked.Remove(piece);
        }
    }

    private DateTimeOffset Now()
    {
        return (time ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>A block arrived.</summary>
    private async Task Took(PeerConnection peer, PeerMessage message, CancellationToken ct)
    {
        (int piece, int offset, byte[] data) = message.AsBlock();

        string who = peer.Introduction.Client;
        PieceOutcome outcome;
        PieceAssembly? assembly;

        lock (_lock)
        {
            _downloaded += data.Length;

            if (!_building.TryGetValue(piece, out assembly))
            {
                // A block for a piece nobody is building: a leftover from a
                // request cancelled by the endgame, and not worth a fault.
                return;
            }

            outcome = assembly.Add(offset, data, who);

            // It is still moving, so it keeps its place. The clock is on the
            // piece and not on the request: a peer sending it a block at a time
            // is slow and not gone.
            _asked[piece] = Now();

            if (outcome == PieceOutcome.Verified)
            {
                // Written first, then counted, then announced. Only a crash
                // between the first two could tell the difference, so no test
                // here can — but a bitfield claiming a piece that never reached
                // the disk is one this client would go on to serve as rubbish.
                disk.Write(piece, assembly.Bytes);
                verified.Set(piece);
                _building.Remove(piece);
                _asked.Remove(piece);
            }
            else if (outcome == PieceOutcome.Failed)
            {
                _trust.Failed(assembly.Contributors);
                _building.Remove(piece);
                _asked.Remove(piece);
            }
        }

        if (outcome == PieceOutcome.Verified)
        {
            await Told(piece, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Everybody hears about a piece that is now here.</summary>
    private async Task Told(int piece, CancellationToken ct)
    {
        PeerConnection[] everybody;

        lock (_lock)
        {
            everybody = [.. _peers];
        }

        foreach (PeerConnection peer in everybody)
        {
            try
            {
                await peer.SendAsync(PeerMessage.Have(piece), ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // A peer that has gone between one message and the next. Its
                // own loop will notice and tidy up.
            }
        }
    }

    /// <summary>A peer asked for a block, and it is there to give.</summary>
    /// <remarks>
    /// Only on a private torrent. The owner's rule is that this client uploads
    /// to their own trackers and to nowhere else, and a public swarm contains
    /// peers that ask whether or not they were unchoked — so the refusal is
    /// here as well as in the unchoke, and it is here that it counts.
    /// </remarks>
    private async Task Serve(PeerConnection peer, PeerMessage message, CancellationToken ct)
    {
        if (!torrent.Private)
        {
            return;
        }

        (int piece, int offset, int length) = message.AsRequest();

        if (piece < 0 || piece >= torrent.PieceCount || !verified.Has(piece) || length > PeerMessage.BlockLength)
        {
            // Asked for something this client has not got, or for more than a
            // block. Neither is worth answering and neither is worth a fault.
            return;
        }

        byte[] block;

        lock (_lock)
        {
            block = disk.Read((piece * torrent.PieceLength) + offset, length);
            _uploaded += block.Length;
        }

        await peer.SendAsync(PeerMessage.Block(piece, offset, block), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// How much is verified, in bytes.
    /// </summary>
    /// <remarks>
    /// Added up piece by piece rather than multiplied out, because the last
    /// piece is short: a client that multiplied would report a torrent as
    /// bigger than it is, and would say a finished one was still going.
    /// </remarks>
    private long Bytes()
    {
        long bytes = 0;

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            if (verified.Has(piece))
            {
                bytes += torrent.LengthOfPiece(piece);
            }
        }

        return bytes;
    }

    public void Dispose()
    {
        disk.Dispose();
    }
}
