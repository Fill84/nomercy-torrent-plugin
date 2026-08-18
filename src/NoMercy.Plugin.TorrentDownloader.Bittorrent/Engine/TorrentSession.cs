namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What one torrent is doing, from the inside.</summary>
/// <param name="Verified">How many pieces are on disk and hashed.</param>
/// <param name="Pieces">How many there are.</param>
/// <param name="BytesDone">How much of it is verified.</param>
/// <param name="Peers">How many connections are up.</param>
/// <param name="Seeds">How many of those have the lot.</param>
/// <param name="Downloaded">Bytes of pieces that have arrived, good and bad.</param>
/// <param name="Uploaded">Bytes of pieces that have gone out.</param>
public sealed record SessionProgress(
    int Verified,
    int Pieces,
    long BytesDone,
    int Peers,
    int Seeds,
    long Downloaded,
    long Uploaded)
{
    /// <summary>Whether every piece is verified.</summary>
    public bool Complete => Verified == Pieces;
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
public sealed class TorrentSession(TorrentMetadata torrent, TorrentDisk disk, Bitfield verified) : IDisposable
{
    private readonly PiecePicker _picker = new(torrent.PieceCount);
    private readonly Dictionary<int, PieceAssembly> _building = [];
    private readonly PeerTrust _trust = new();
    private readonly List<PeerConnection> _peers = [];
    private readonly Lock _lock = new();
    private readonly Random _random = new();
    private long _downloaded;
    private long _uploaded;

    /// <summary>The torrent this is for.</summary>
    public TorrentMetadata Torrent => torrent;

    /// <summary>What is verified on disk.</summary>
    public Bitfield Verified => verified;

    /// <summary>Whether every piece is there.</summary>
    public bool Complete => verified.All;

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
                _uploaded);
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

        try
        {
            await peer.SendAsync(new(PeerMessageId.Bitfield, verified.Write()), ct).ConfigureAwait(false);

            // Unchoked from the start. Choking properly is a decision across
            // every peer at once and belongs to the choking round; refusing
            // everybody until it has run would leave a two-peer swarm silent.
            await peer.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), ct).ConfigureAwait(false);

            if (!Complete)
            {
                await peer.SendAsync(PeerMessage.Of(PeerMessageId.Interested), ct).ConfigureAwait(false);
            }

            await Turn(peer, ct).ConfigureAwait(false);

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

        int? next;

        lock (_lock)
        {
            _picker.Saw(peer.Has);

            next = _picker.Next(verified, peer.Has, _building.Keys.ToHashSet(), _random);

            // Undone straight away: availability is counted once per peer, and
            // this is asked on every message that arrives.
            _picker.Left(peer.Has);

            if (next is int piece && !_building.ContainsKey(piece))
            {
                _building[piece] = new(piece, torrent.LengthOfPiece(piece), torrent.Pieces[piece]);
            }
        }

        if (next is not int wanted)
        {
            return;
        }

        foreach (BlockRequest block in PiecePicker.Blocks(wanted, torrent.LengthOfPiece(wanted)))
        {
            await peer.SendAsync(PeerMessage.Request(block.Piece, block.Offset, block.Length), ct).ConfigureAwait(false);
        }
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

            if (outcome == PieceOutcome.Verified)
            {
                // Written first, then counted, then announced. Only a crash
                // between the first two could tell the difference, so no test
                // here can — but a bitfield claiming a piece that never reached
                // the disk is one this client would go on to serve as rubbish.
                disk.Write(piece, assembly.Bytes);
                verified.Set(piece);
                _building.Remove(piece);
            }
            else if (outcome == PieceOutcome.Failed)
            {
                _trust.Failed(assembly.Contributors);
                _building.Remove(piece);
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
    private async Task Serve(PeerConnection peer, PeerMessage message, CancellationToken ct)
    {
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
