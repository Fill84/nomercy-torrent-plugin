namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// One conversation with one peer.
/// </summary>
/// <remarks>
/// <para>
/// It owns the stream, the reader that reassembles messages out of it, and the
/// four facts BEP 3 keeps per connection: whether each end is choking the other
/// and whether each is interested. Everything above it — which piece to ask
/// for, what to do with one that arrives — belongs to the session, because
/// those are decisions about the torrent rather than about this peer.
/// </para>
/// <para>
/// A peer that says something a client cannot use is dropped rather than
/// argued with. There are always more peers, and a connection kept alive out of
/// politeness is a connection sending nothing.
/// </para>
/// </remarks>
public sealed class PeerConnection(Stream wire, PeerHandshake introduction, int pieces) : IDisposable
{
    private readonly PeerMessageReader _reader = new();
    private readonly byte[] _buffer = new byte[64 * 1024];

    /// <summary>
    /// One send at a time, held from the write until the flush is done.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The keep-alive beat, the read loop answering a request and every other
    /// peer's conversation announcing a verified piece all write here without
    /// waiting for each other. On an encrypted connection the RC4 keystream is
    /// one running state: two sends encrypting at once tear it, and two that
    /// encrypt in one order and reach the socket in the other put bytes on the
    /// wire out of keystream order. Everything after that decrypts to rubbish
    /// at the peer, which drops the connection. The upload counter lost
    /// increments the same way.
    /// </para>
    /// <para>
    /// Never disposed. A <see cref="SemaphoreSlim"/> holds nothing to release
    /// unless its wait handle is asked for, and disposing it would strand a
    /// send already waiting for its turn: disposal wakes no waiter, so that
    /// send would hang for ever instead of failing on the closed stream with
    /// the <see cref="ObjectDisposedException"/> or <see cref="IOException"/>
    /// every caller already handles.
    /// </para>
    /// </remarks>
    private readonly SemaphoreSlim _sending = new(1, 1);

    /// <summary>
    /// How many <c>have</c>s a peer may say before the piece count is known.
    /// </summary>
    /// <remarks>
    /// They are kept until then (<see cref="Sized"/>), and the metadata of a
    /// magnet is seconds away, in which a peer finishes a handful of pieces. A
    /// peer that says more than this before anybody can check a word of it is
    /// not kept a list for.
    /// </remarks>
    public const int MostEarlyHaves = 4096;

    /// <summary>
    /// Held wherever <see cref="Has"/> is replaced or written: by the read
    /// that takes a bitfield or a <c>have</c>, and by <see cref="Sized"/>,
    /// which comes from the peer's conversation while a read it began earlier
    /// may still be going.
    /// </summary>
    private readonly Lock _having = new();

    /// <summary>The pieces a peer announced before the piece count was known.</summary>
    private readonly List<int> _early = [];

    /// <summary>Who they said they were.</summary>
    public PeerHandshake Introduction => introduction;

    /// <summary>What they have said they have.</summary>
    public Bitfield Has { get; private set; } = new(pieces);

    /// <summary>Whether they are choking us, which is where every connection starts.</summary>
    public bool Choked { get; private set; } = true;

    /// <summary>Whether they want anything we have.</summary>
    public bool Interested { get; private set; }

    /// <summary>Whether we are choking them.</summary>
    public bool Choking { get; private set; } = true;

    /// <summary>How many bytes of pieces have arrived from them.</summary>
    public long Downloaded { get; private set; }

    /// <summary>And how many have gone the other way.</summary>
    public long Uploaded { get; private set; }

    /// <summary>Whether they have the lot.</summary>
    public bool Seed => Has.All;

    /// <summary>
    /// Tells a connection made for a magnet how many pieces the torrent has,
    /// now that the metadata says.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A peer says what it has the moment the handshake is done, which for a
    /// magnet is before this side can count the pieces. Its bitfield was kept
    /// as it came, a bit for every bit of its bytes, and never looked at again:
    /// a seed of 379 pieces had 379 out of 384 for as long as it stayed and was
    /// counted as a leecher, and a peer that had sent no bitfield had nought
    /// out of nought, which is all of it, and was counted as a seed. On
    /// 19 November 2026 the owner's page read "0 of 7" seeds beside "5 of 1"
    /// peers on a download arriving at seventy megabytes a second.
    /// </para>
    /// <para>
    /// So what was said early is read again here against the real count, by
    /// the rule every later bitfield is read by: the wrong number of bytes, or
    /// a bit set past the end, is a peer talking about another torrent, and it
    /// throws. The <c>have</c>s that came early are set on top.
    /// </para>
    /// <para>
    /// Nothing happens on a connection that knew the count when it was made.
    /// </para>
    /// </remarks>
    public void Sized(int count)
    {
        lock (_having)
        {
            if (pieces > 0 || count <= 0)
            {
                return;
            }

            byte[] said = Has.Write();
            Bitfield has = said.Length == 0 ? new(count) : Bitfield.Read(said, count);

            foreach (int piece in _early)
            {
                has.Set(piece);
            }

            _early.Clear();
            pieces = count;
            Has = has;
        }
    }

    /// <summary>Sends one message.</summary>
    public async Task SendAsync(PeerMessage message, CancellationToken ct)
    {
        byte[] frame = message.Write();

        await _sending.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (message.Id == PeerMessageId.Piece)
            {
                // The payload is the piece index, the offset and then the block, so
                // what really went out is what is left after those eight bytes.
                Uploaded += Math.Max(0, message.Payload.Length - 8);
            }

            if (message.Id == PeerMessageId.Unchoke)
            {
                Choking = false;
            }

            if (message.Id == PeerMessageId.Choke)
            {
                Choking = true;
            }

            await wire.WriteAsync(frame, ct).ConfigureAwait(false);
            await wire.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// The next message, with everything this connection keeps track of already
    /// brought up to date.
    /// </summary>
    /// <remarks>
    /// Null when the peer has hung up. Choke, unchoke, interested, bitfield and
    /// have are acted on here — they are facts about this connection rather
    /// than about the torrent — and then handed on anyway, because the session
    /// has to be told <em>something happened</em>: an unchoke that was swallowed
    /// here is a session that never asks for a piece and a download that never
    /// starts. That was a real deadlock, found by the end-to-end test.
    /// </remarks>
    public async Task<PeerMessage?> NextAsync(CancellationToken ct)
    {
        while (true)
        {
            PeerMessage? message = _reader.Next();

            if (message is null)
            {
                int read = await wire.ReadAsync(_buffer, ct).ConfigureAwait(false);

                if (read == 0)
                {
                    return null;
                }

                _reader.Add(_buffer.AsSpan(0, read));

                continue;
            }

            switch (message.Id)
            {
                case null:
                    // A keep-alive, which is four bytes of nought and not a
                    // fault. Waiting for the next real message.
                    continue;

                case PeerMessageId.Choke:
                    Choked = true;
                    return message;

                case PeerMessageId.Unchoke:
                    Choked = false;
                    return message;

                case PeerMessageId.Interested:
                    Interested = true;
                    return message;

                case PeerMessageId.NotInterested:
                    Interested = false;
                    return message;

                case PeerMessageId.Bitfield:
                    // Checked against the piece count, unless there is not one
                    // yet. A client that took a magnet on knows the info hash
                    // and nothing else — it dials with nought pieces, because
                    // the metadata saying how many there are is the very thing
                    // it is dialling for — and nearly every peer sends its
                    // bitfield the moment the handshake is done.
                    //
                    // A bitfield for nought pieces is nought bytes, so every one
                    // of them read as a protocol violation and took the peer
                    // with it: a swarm of 1206 seeders answered nine of 175
                    // dials on 26 August 2026 and all nine were destroyed on
                    // their first message, leaving the page saying no peers, no
                    // seeds and no error for hours.
                    //
                    // Taken as it comes until there is something to check it
                    // against. What a peer has cannot be acted on before the
                    // metadata anyway: there are no pieces to ask it for. It
                    // is checked, and cut to the torrent's own length, the
                    // moment the count is known (Sized).
                    lock (_having)
                    {
                        Has = pieces > 0
                            ? Bitfield.Read(message.Payload, pieces)
                            : Bitfield.Read(message.Payload, message.Payload.Length * 8);
                    }

                    return message;

                case PeerMessageId.Have:
                    lock (_having)
                    {
                        if (pieces > 0)
                        {
                            Has.Set(message.AsHave());
                        }
                        else if (_early.Count < MostEarlyHaves)
                        {
                            // The same rule: a have for a torrent nobody can
                            // size yet is not a peer misbehaving, and it is
                            // not thrown away either. It was set only when its
                            // number was below how many pieces the peer already
                            // had, which is no rule at all: a peer with ten
                            // pieces that finished its five-hundredth was
                            // never asked for it.
                            _early.Add(message.AsHave());
                        }
                    }

                    return message;

                case PeerMessageId.Piece:
                    Downloaded += Math.Max(0, message.Payload.Length - 8);

                    return message;

                default:
                    return message;
            }
        }
    }

    /// <summary>Reads the handshake off the front of a connection and answers it.</summary>
    /// <remarks>
    /// The info hash has to be the one this connection is for. A peer offering
    /// another torrent is not confused, it is a different swarm — BEP 3 says to
    /// drop it, and writing its blocks into these files would be writing
    /// somebody else's bytes.
    /// </remarks>
    public static async Task<PeerConnection?> IntroduceAsync(
        Stream wire,
        byte[] infoHash,
        byte[] peerId,
        int pieces,
        bool dialling,
        CancellationToken ct)
    {
        if (dialling)
        {
            await wire.WriteAsync(Handshake.Write(infoHash, peerId), ct).ConfigureAwait(false);
            await wire.FlushAsync(ct).ConfigureAwait(false);
        }

        byte[] theirs = new byte[Handshake.Length];

        try
        {
            await wire.ReadExactlyAsync(theirs, ct).ConfigureAwait(false);
        }
        catch (Exception hungUp) when (hungUp is EndOfStreamException or IOException)
        {
            // A peer that hung up before saying anything, which is most of
            // them. An abortive close comes back as an IOException rather than
            // an end of stream, and both mean the same thing here.
            return null;
        }

        if (Handshake.Read(theirs) is not PeerHandshake introduction
            || !introduction.InfoHash.AsSpan().SequenceEqual(infoHash))
        {
            return null;
        }

        if (!dialling)
        {
            await wire.WriteAsync(Handshake.Write(infoHash, peerId), ct).ConfigureAwait(false);
            await wire.FlushAsync(ct).ConfigureAwait(false);
        }

        return new(wire, introduction, pieces);
    }

    /// <summary>
    /// Makes a connection out of a handshake that has already been read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An encrypted dial sends this client's handshake inside the encryption
    /// negotiation, so by the time there is a connection to make, ours has gone
    /// and theirs has come back — along with whatever the peer sent after it,
    /// because a peer that answers in one round trip puts its first message in
    /// the same write.
    /// </para>
    /// <para>
    /// Those extra bytes are kept and read before the wire. Thrown away, a peer
    /// whose bitfield arrived with its handshake would look like a peer that
    /// has nothing, and would never be asked for a piece.
    /// </para>
    /// </remarks>
    /// <param name="wire">The stream, with the handshake already off it.</param>
    /// <param name="infoHash">Which torrent this connection is for.</param>
    /// <param name="pieces">How many pieces the torrent has.</param>
    /// <param name="already">Their handshake, and anything that came with it.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<PeerConnection?> IntroducedAsync(
        Stream wire,
        byte[] infoHash,
        int pieces,
        ReadOnlyMemory<byte> already,
        CancellationToken ct)
    {
        if (already.Length < Handshake.Length)
        {
            // The rest of it is still on the wire. A peer that sent half a
            // handshake and stopped is one that hung up, which is most of them.
            byte[] rest = new byte[Handshake.Length - already.Length];

            try
            {
                await wire.ReadExactlyAsync(rest, ct).ConfigureAwait(false);
            }
            catch (Exception hungUp) when (hungUp is EndOfStreamException or IOException)
            {
                return null;
            }

            already = (byte[])[.. already.Span, .. rest];
        }

        if (Handshake.Read(already.Span[..Handshake.Length]) is not PeerHandshake introduction
            || !Handshake.IsFor(introduction, infoHash))
        {
            return null;
        }

        return new(new HeadStart(wire, already[Handshake.Length..]), introduction, pieces);
    }

    public void Dispose()
    {
        wire.Dispose();
    }
}
