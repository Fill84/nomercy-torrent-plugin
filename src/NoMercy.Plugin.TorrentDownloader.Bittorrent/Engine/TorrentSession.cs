namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What one torrent is doing, from the inside.</summary>
/// <param name="Verified">How many pieces are on disk and hashed.</param>
/// <param name="Pieces">How many there are.</param>
/// <param name="BytesDone">How much of it is verified.</param>
/// <param name="Leechers">
/// How many of the connections are leechers — a peer that does not have all of
/// it. Counted, never worked out from the others: seeds and leechers are two
/// populations and subtracting one from the other ties them together again.
/// </param>
/// <param name="Peers">How many connections are up.</param>
/// <param name="Seeds">How many of those have the lot.</param>
/// <param name="Askable">
/// How many of the connected peers have unchoked this client <em>and</em> hold at
/// least one piece it still wants. It is the number that says whether a torrent
/// standing still is the swarm's doing or this client's: peers that will not talk
/// and peers with nothing to offer both leave it at nought, and so does a client
/// that is not asking, and only this tells them apart.
/// </param>
/// <param name="ChokedBy">
/// How many of those will not send anything. A peer starts choked by BEP 3 and
/// stays that way until it says otherwise; on a public torrent this client never
/// unchokes anybody, so a well-behaved peer has no reason to unchoke it either.
/// Thirty peers none of which will talk and thirty that are merely slow look the
/// same without this.
/// </param>
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
    int Leechers,
    int ChokedBy,
    int Askable,
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
/// <param name="limits">
/// The owner's rate limits, shared with every other torrent, or null for none.
/// </param>
/// <param name="met">
/// Told about peers this session was offered by the ones it is talking to, or
/// null to ignore them. It is handed up rather than acted on here, because
/// this session owns no sockets.
/// </param>
public sealed class TorrentSession(
    TorrentMetadata torrent,
    TorrentDisk disk,
    Bitfield verified,
    Bitfield? wanted = null,
    TimeSpan? patience = null,
    TimeProvider? time = null,
    RateLimits? limits = null,
    Action<IReadOnlyList<PeerAddress>>? met = null) : IDisposable
{
    private readonly PiecePicker _picker = new(torrent.PieceCount, PiecePicker.DefaultEndgamePieces, wanted);
    private readonly Dictionary<int, PieceAssembly> _building = [];

    /// <summary>Which peer each piece being built was claimed for.</summary>
    /// <remarks>
    /// <see cref="Pipeline"/> says how many pieces are asked of one peer at a
    /// time, and it was counted per call rather than per peer. The asking runs
    /// on every message a peer sends, and each run claimed up to four more
    /// pieces — a buffer the size of a whole piece each, held until the piece
    /// arrived, failed its hash, or sat unanswered for the abandon patience of
    /// a minute. A peer that talks without sending blocks therefore walked this
    /// client through the entire file list: on 30 August 2026 a 36.1 GB season
    /// pack put the media server at 45 GB of memory while showing nought per
    /// cent, because it had claimed a buffer for very nearly every piece of
    /// itself.
    ///
    /// So the count is per peer now, and this is what it is counted from.
    /// </remarks>
    private readonly Dictionary<int, PeerConnection> _claimedFor = [];

    /// <summary>When each piece being built last heard anything.</summary>
    private readonly Dictionary<int, DateTimeOffset> _asked = [];
    private readonly PeerTrust _trust = new();

    /// <summary>
    /// What each peer has been asked for, and may therefore send.
    /// </summary>
    /// <remarks>
    /// A block nobody asked for is not a gift: it is memory this process did
    /// not plan to spend, written at an offset nothing is expecting, and the
    /// peer sending it is either broken or trying something. One ledger per
    /// peer, because two peers may hold a request for the same block — the
    /// endgame asks several on purpose.
    /// </remarks>
    private readonly Dictionary<PeerConnection, RequestLedger> _ledgers = [];
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
                _peers.Count(one => !one.Seed),

                // Choked until told otherwise, which is BEP 3 — so this counts
                // the peers that have said nothing as well as the ones that
                // said no, and both mean the same thing: nothing will come.
                _peers.Count(one => one.Choked),

                // Unchoked and holding something still wanted. Counted here
                // rather than worked out on a page, because what is wanted is
                // the verified bitfield and nothing outside this class has it.
                _peers.Count(one => !one.Choked && Wanted(one)),
                _downloaded,
                _uploaded,
                WantedBytes);
        }
    }

    /// <summary>Whether this peer holds a piece this client still wants.</summary>
    /// <remarks>
    /// Only the wanted pieces: a torrent whose sample files are skipped is
    /// never complete against its own piece count, and a peer holding nothing
    /// but those has nothing for this client.
    /// </remarks>
    private bool Wanted(PeerConnection peer)
    {
        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            if (!verified.Has(piece) && peer.Has.Has(piece) && (wanted is null || wanted.Has(piece)))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Talks to one peer until the torrent is finished or the peer goes.
    /// </summary>
    /// <remarks>
    /// Both halves at once: it asks for what it is missing and answers what it
    /// is asked for. A client that only downloaded would be choked by every
    /// well-behaved peer in the swarm within a minute.
    /// </remarks>
    /// <param name="peer">The peer to talk to.</param>
    /// <param name="ct">Stops the conversation.</param>
    /// <param name="pending">
    /// A read of this peer already under way, from whoever held the connection
    /// before. Awaited as the first message rather than started again: two
    /// reads on one connection take each other's bytes.
    /// </param>
    public async Task RunAsync(PeerConnection peer, CancellationToken ct, Task<PeerMessage?>? pending = null)
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
                PeerMessage? message = pending is not null
                    ? await pending.ConfigureAwait(false)
                    : await peer.NextAsync(ct).ConfigureAwait(false);

                pending = null;

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

                // Its ledger goes with it. What it was asked for is not owed by
                // whoever dials in next.
                _ledgers.Remove(peer);

                // And its claims stop counting against it, so the peer that
                // takes its place is not held to a pipeline full of pieces
                // nobody is going to send. The half-built pieces themselves
                // stay where they are: the abandon sweep is what gives those
                // back, and it knows to throw away what was assembled.
                foreach (int piece in _claimedFor.Where(one => one.Value == peer).Select(one => one.Key).ToArray())
                {
                    _claimedFor.Remove(piece);
                }
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

    /// <summary>What this peer has been asked for. Under the lock.</summary>
    private RequestLedger Ledger(PeerConnection peer)
    {
        if (!_ledgers.TryGetValue(peer, out RequestLedger? ledger))
        {
            ledger = new();
            _ledgers[peer] = ledger;
        }

        return ledger;
    }

    /// <summary>What to do with one message from one peer.</summary>
    private async Task Handle(PeerConnection peer, PeerMessage message, CancellationToken ct)
    {
        // Peer exchange, before anything this session itself wants. A swarm is
        // learned from the peers already in it: a tracker's fifty addresses are
        // mostly stale, and without this a download runs on the one or two that
        // answered. Handed up rather than acted on here, because the sockets
        // belong to the run.
        if (message.Id == PeerMessageId.Extended
            && message.Payload.Length > 0
            && message.Payload[0] == Extensions.OurExchangeId
            && met is not null)
        {
            // BEP 27: a private torrent looks for peers on its own tracker and
            // nowhere else, and that holds for peers offered to it as much as
            // for peers it goes looking for. PeerExchange.Read has this guard;
            // this called the static Pex.Read, which has none — so the outgoing
            // half was refused and the incoming half was taken and dialled. A
            // private tracker that catches that bans the account.
            if (torrent.Private)
            {
                return;
            }

            PexUpdate offered = Pex.Read(message);

            if (offered.Added.Count > 0)
            {
                met(offered.Added);
            }

            return;
        }

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

            // What this peer is already on the hook for. Pieces claimed for
            // somebody else do not count against it, and its own do — however
            // many messages ago they were claimed.
            int held = 0;

            foreach (PeerConnection one in _claimedFor.Values)
            {
                if (one == peer)
                {
                    held++;
                }
            }

            while (held + asking.Count < Pipeline)
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

                _building[piece] = new(piece, torrent.LengthOfPiece(piece), torrent.Pieces[piece], disk);
                _claimedFor[piece] = peer;
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
                lock (_lock)
                {
                    // Written down before it is sent. A block that arrived
                    // between the send and the note would be refused as
                    // unasked-for, which is the one way this guard could cost a
                    // real download a block.
                    Ledger(peer).Asked(block.Piece, block.Offset, block.Length);
                }

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
            _claimedFor.Remove(piece);
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
            if (!Ledger(peer).Accept(piece, offset, data.Length))
            {
                // Nobody asked this peer for this. Dropped rather than
                // accommodated, and not counted: a block written at an offset
                // nothing is expecting is how a peer that is broken — or
                // trying something — spends this process's memory.
                return;
            }

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
                // On the disk already, block by block as they arrived, and the
                // hash that says Verified was taken by reading it back — so by
                // here the bytes are down and this only records that they are.
                // A bitfield claiming a piece that never reached the disk is
                // one this client would go on to serve as rubbish.
                verified.Set(piece);
                _building.Remove(piece);
                _claimedFor.Remove(piece);
                _asked.Remove(piece);
            }
            else if (outcome == PieceOutcome.Failed)
            {
                _trust.Failed(assembly.Contributors);
                _building.Remove(piece);
                _claimedFor.Remove(piece);
                _asked.Remove(piece);
            }
        }

        if (limits is not null)
        {
            // After it has arrived, because a block cannot be un-received. What
            // it buys is the pause before this peer is asked for the next one,
            // which is what a peer reads as "slow down".
            await limits.PassAsync(downloading: true, torrent.InfoHash, data.Length, ct).ConfigureAwait(false);
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

        if (limits is not null)
        {
            // Before it goes, not after: a limit applied afterwards has already
            // let the bytes out.
            await limits.PassAsync(downloading: false, torrent.InfoHash, block.Length, ct).ConfigureAwait(false);
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
