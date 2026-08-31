using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// A connection to a peer, once both ends have agreed how they will talk.
/// </summary>
/// <param name="Stream">
/// Read and written in the clear. When RC4 was agreed this wraps the socket and
/// does the cipher; when plaintext was agreed it is the socket.
/// </param>
/// <param name="Method">Which of the two was agreed.</param>
/// <param name="InfoHash">Which torrent the connection is for.</param>
/// <param name="Initial">
/// The bytes the other end sent with its handshake, which is where a peer puts
/// its BitTorrent handshake so that the whole introduction is one round trip.
/// </param>
public sealed record MseLink(Stream Stream, MseMethod Method, byte[] InfoHash, byte[] Initial);

/// <summary>
/// The MSE handshake, from either end.
/// </summary>
/// <remarks>
/// <para>
/// Diffie-Hellman first, then a hash that says which torrent without putting
/// the info hash on the wire, then RC4 or plaintext by agreement. Neither side
/// knows how much padding the other sent, so both have to find their footing in
/// the stream by looking for something they can predict — the receiver looks
/// for <c>req1</c>, the initiator for the encrypted verification constant.
/// </para>
/// <para>
/// It is obfuscation and not security. What it buys is peers: a great many
/// refuse a connection that arrives in the clear.
/// </para>
/// </remarks>
public static class MseNegotiation
{
    /// <summary>How long a hash is, everywhere in this handshake.</summary>
    private const int HashLength = 20;

    /// <summary>
    /// How much of the answer says whether the peer is speaking MSE at all.
    /// </summary>
    /// <remarks>
    /// The length byte and the protocol name. A peer that will not do MSE sends
    /// its handshake and waits, and sixty-eight bytes is fewer than a key.
    /// </remarks>
    private const int PlaintextTell = 1 + 19;

    /// <summary>Dials: sends the key, says which torrent, and offers both methods.</summary>
    /// <param name="wire">The socket, or anything that behaves like one.</param>
    /// <param name="infoHash">Which torrent, which is also the key to the cipher.</param>
    /// <param name="payload">Sent with the handshake — this client's BitTorrent handshake.</param>
    /// <param name="provide">What to offer. Both, unless a test is asking for one.</param>
    /// <param name="random">Where the key and the padding come from.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<MseLink> InitiateAsync(
        Stream wire,
        byte[] infoHash,
        byte[] payload,
        MseMethod provide,
        RandomNumberGenerator random,
        CancellationToken ct)
    {
        MseKeyPair ours = MseKeyPair.Create(random);

        await wire.WriteAsync(ours.Public, ct).ConfigureAwait(false);
        await wire.WriteAsync(Padding(random), ct).ConfigureAwait(false);
        await wire.FlushAsync(ct).ConfigureAwait(false);

        byte[] theirs = new byte[Mse.KeyLength];

        // The first twenty bytes on their own, because a peer that will not do
        // MSE answers with its sixty-eight byte handshake and then waits. A
        // client that insisted on ninety-six would sit there until the
        // connection timed out and would never learn why.
        await wire.ReadExactlyAsync(theirs.AsMemory(0, PlaintextTell), ct).ConfigureAwait(false);

        if (LooksLikeAPlaintextHandshake(theirs))
        {
            // Said as its own refusal so the caller can dial it again in the
            // clear rather than treat it as a fault.
            throw new MseRefusedException("The peer answered the key exchange with a plaintext handshake.");
        }

        await wire.ReadExactlyAsync(theirs.AsMemory(PlaintextTell), ct).ConfigureAwait(false);

        byte[] secret = ours.Secret(theirs);

        Rc4 outgoing = new(Mse.KeyA(secret, infoHash));
        Rc4 incoming = new(Mse.KeyB(secret, infoHash));

        byte[] padC = Padding(random);

        // Everything from the verification constant on is encrypted, and the
        // initial payload goes with it: one round trip for the whole
        // introduction rather than two.
        byte[] encrypted =
        [
            .. Mse.Verification,
            .. Number((int)provide, 4),
            .. Number(padC.Length, 2),
            .. padC,
            .. Number(payload.Length, 2),
            .. payload,
        ];

        outgoing.Apply(encrypted);

        await wire.WriteAsync(Mse.Req1(secret), ct).ConfigureAwait(false);
        await wire.WriteAsync(Mse.Req2Xor3(infoHash, secret), ct).ConfigureAwait(false);
        await wire.WriteAsync(encrypted, ct).ConfigureAwait(false);
        await wire.FlushAsync(ct).ConfigureAwait(false);

        // The receiver's padding is its own business and its length is nowhere
        // on the wire, so the verification constant is what says where its
        // answer starts.
        await SynchroniseAsync(wire, Encrypted(incoming, Mse.Verification), ct).ConfigureAwait(false);

        byte[] answer = new byte[6];

        await wire.ReadExactlyAsync(answer, ct).ConfigureAwait(false);

        incoming.Apply(answer);

        MseMethod chosen = (MseMethod)BinaryPrimitives.ReadInt32BigEndian(answer);

        if ((chosen & provide) != chosen || chosen == MseMethod.None)
        {
            throw new PeerProtocolException($"The peer chose a method that was not offered: {(int)chosen}.");
        }

        await SkipAsync(wire, BinaryPrimitives.ReadUInt16BigEndian(answer.AsSpan(4)), incoming, ct)
            .ConfigureAwait(false);

        return new(Wrap(wire, chosen, outgoing, incoming), chosen, infoHash, []);
    }

    /// <summary>Answers a dial: finds which torrent it is for and agrees a method.</summary>
    /// <param name="wire">The socket, or anything that behaves like one.</param>
    /// <param name="torrents">The hashes this client is holding. One of them has to match.</param>
    /// <param name="allow">What this end will agree to.</param>
    /// <param name="random">Where the key and the padding come from.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<MseLink> AcceptAsync(
        Stream wire,
        IReadOnlyCollection<byte[]> torrents,
        MseMethod allow,
        RandomNumberGenerator random,
        CancellationToken ct)
    {
        MseKeyPair ours = MseKeyPair.Create(random);

        byte[] theirs = new byte[Mse.KeyLength];

        await wire.ReadExactlyAsync(theirs, ct).ConfigureAwait(false);

        byte[] secret = ours.Secret(theirs);

        await wire.WriteAsync(ours.Public, ct).ConfigureAwait(false);
        await wire.WriteAsync(Padding(random), ct).ConfigureAwait(false);
        await wire.FlushAsync(ct).ConfigureAwait(false);

        // The initiator's padding came before anything predictable, so req1 is
        // what says where the rest of its message begins.
        await SynchroniseAsync(wire, Mse.Req1(secret), ct).ConfigureAwait(false);

        byte[] which = new byte[HashLength];

        await wire.ReadExactlyAsync(which, ct).ConfigureAwait(false);

        byte[] infoHash = Match(which, secret, torrents)
                          ?? throw new PeerProtocolException("The peer asked for a torrent this client is not holding.");

        Rc4 incoming = new(Mse.KeyA(secret, infoHash));
        Rc4 outgoing = new(Mse.KeyB(secret, infoHash));

        byte[] opening = new byte[Mse.Verification.Length + 6];

        await wire.ReadExactlyAsync(opening, ct).ConfigureAwait(false);

        incoming.Apply(opening);

        if (!opening.AsSpan(0, Mse.Verification.Length).SequenceEqual(Mse.Verification))
        {
            // The one proof both ends arrived at the same secret.
            throw new PeerProtocolException("The peer's verification constant did not come out as nought.");
        }

        MseMethod offered = (MseMethod)BinaryPrimitives.ReadInt32BigEndian(opening.AsSpan(Mse.Verification.Length));
        MseMethod chosen = Choose(offered & allow);

        await SkipAsync(wire, BinaryPrimitives.ReadUInt16BigEndian(opening.AsSpan(Mse.Verification.Length + 4)), incoming, ct)
            .ConfigureAwait(false);

        byte[] length = new byte[2];

        await wire.ReadExactlyAsync(length, ct).ConfigureAwait(false);

        incoming.Apply(length);

        byte[] initial = new byte[BinaryPrimitives.ReadUInt16BigEndian(length)];

        await wire.ReadExactlyAsync(initial, ct).ConfigureAwait(false);

        incoming.Apply(initial);

        byte[] padD = Padding(random);

        byte[] answer =
        [
            .. Mse.Verification,
            .. Number((int)chosen, 4),
            .. Number(padD.Length, 2),
            .. padD,
        ];

        outgoing.Apply(answer);

        await wire.WriteAsync(answer, ct).ConfigureAwait(false);
        await wire.FlushAsync(ct).ConfigureAwait(false);

        return new(Wrap(wire, chosen, outgoing, incoming), chosen, infoHash, initial);
    }

    /// <summary>
    /// RC4 was agreed, or it was not.
    /// </summary>
    /// <remarks>
    /// When plaintext is agreed the handshake itself was still encrypted and
    /// everything after it is not, which is the point of offering both: the
    /// expensive part is over and the connection costs nothing to keep.
    /// </remarks>
    private static Stream Wrap(Stream wire, MseMethod chosen, Rc4 outgoing, Rc4 incoming)
    {
        return chosen == MseMethod.Rc4 ? new Rc4Stream(wire, outgoing, incoming) : wire;
    }

    /// <summary>RC4 when it is on offer, and plaintext when it is not.</summary>
    private static MseMethod Choose(MseMethod both)
    {
        if (both.HasFlag(MseMethod.Rc4))
        {
            return MseMethod.Rc4;
        }

        return both.HasFlag(MseMethod.Plaintext)
            ? MseMethod.Plaintext
            : throw new PeerProtocolException("The peer offered no method this client will use.");
    }

    /// <summary>Which torrent this is, out of the ones being held.</summary>
    private static byte[]? Match(byte[] which, byte[] secret, IReadOnlyCollection<byte[]> torrents)
    {
        foreach (byte[] candidate in torrents)
        {
            if (which.AsSpan().SequenceEqual(Mse.Req2Xor3(candidate, secret)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Reads until these bytes have gone past, and stops just after them.</summary>
    /// <remarks>
    /// Byte at a time, because the padding in front of them can be any length
    /// up to five hundred and twelve and none of it is announced. Bounded by
    /// that same number: a peer that never sends the pattern would otherwise
    /// have this client read until the connection died.
    /// </remarks>
    private static async Task SynchroniseAsync(Stream wire, byte[] wanted, CancellationToken ct)
    {
        byte[] window = new byte[wanted.Length];
        byte[] one = new byte[1];
        int have = 0;

        for (int read = 0; read <= Mse.MostPadding + wanted.Length; read++)
        {
            await wire.ReadExactlyAsync(one, ct).ConfigureAwait(false);

            if (have == window.Length)
            {
                Array.Copy(window, 1, window, 0, window.Length - 1);
                have--;
            }

            window[have++] = one[0];

            if (have == window.Length && window.AsSpan().SequenceEqual(wanted))
            {
                return;
            }
        }

        throw new PeerProtocolException("The peer sent more padding than MSE allows, or nothing this client could find.");
    }

    /// <summary>Reads padding and throws it away, keeping the keystream in step.</summary>
    private static async Task SkipAsync(Stream wire, int length, Rc4 cipher, CancellationToken ct)
    {
        if (length > Mse.MostPadding)
        {
            throw new PeerProtocolException($"A peer claimed {length} bytes of padding, and MSE allows {Mse.MostPadding}.");
        }

        if (length == 0)
        {
            return;
        }

        byte[] padding = new byte[length];

        await wire.ReadExactlyAsync(padding, ct).ConfigureAwait(false);

        // Through the cipher even though it is discarded: the keystream is one
        // continuous thing and skipping these bytes would leave every byte
        // after them wrong.
        cipher.Apply(padding);
    }

    /// <summary>Whether what came back is a BitTorrent handshake rather than a key.</summary>
    private static bool LooksLikeAPlaintextHandshake(ReadOnlySpan<byte> bytes)
    {
        return bytes[0] == Handshake.Protocol.Length
               && bytes[1..(1 + Handshake.Protocol.Length)].SequenceEqual(Handshake.Protocol);
    }

    /// <summary>These bytes as that cipher would send them.</summary>
    private static byte[] Encrypted(Rc4 cipher, ReadOnlySpan<byte> bytes)
    {
        byte[] copy = bytes.ToArray();

        cipher.Apply(copy);

        return copy;
    }

    /// <summary>Nought to five hundred and twelve bytes of nothing in particular.</summary>
    private static byte[] Padding(RandomNumberGenerator random)
    {
        byte[] length = new byte[2];

        random.GetBytes(length);

        byte[] padding = new byte[BinaryPrimitives.ReadUInt16BigEndian(length) % (Mse.MostPadding + 1)];

        random.GetBytes(padding);

        return padding;
    }

    /// <summary>A number, big-endian, in as many bytes as MSE puts it in.</summary>
    private static byte[] Number(int value, int bytes)
    {
        byte[] written = new byte[bytes];

        if (bytes == 2)
        {
            BinaryPrimitives.WriteUInt16BigEndian(written, (ushort)value);
        }
        else
        {
            BinaryPrimitives.WriteInt32BigEndian(written, value);
        }

        return written;
    }
}

/// <summary>A peer that will not do MSE, which is not the same as a peer that is broken.</summary>
/// <remarks>
/// Its own exception so a caller can dial it again in the clear. Encryption is
/// allowed and never required.
/// </remarks>
public sealed class MseRefusedException(string message) : Exception(message);

/// <summary>
/// A stream with RC4 over it, one keystream each way.
/// </summary>
/// <remarks>
/// The two directions are different keys — <c>keyA</c> out, <c>keyB</c> back —
/// and neither keystream may skip a byte, so every byte read and every byte
/// written passes through exactly once.
/// </remarks>
public sealed class Rc4Stream(Stream inner, Rc4 outgoing, Rc4 incoming) : Stream
{
    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int read = inner.Read(buffer, offset, count);

        incoming.Apply(buffer.AsSpan(offset, read));

        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);

        incoming.Apply(buffer.Span[..read]);

        return read;
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        byte[] copy = buffer.AsSpan(offset, count).ToArray();

        outgoing.Apply(copy);
        inner.Write(copy, 0, copy.Length);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // A copy, never in place: the caller's buffer is the caller's, and a
        // client that encrypted a shared array would corrupt whatever else was
        // looking at it.
        byte[] copy = buffer.ToArray();

        outgoing.Apply(copy);

        await inner.WriteAsync(copy, ct).ConfigureAwait(false);
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken ct)
    {
        return inner.FlushAsync(ct);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>
/// Dialling a peer: encrypted first, and in the clear when it will not.
/// </summary>
/// <remarks>
/// docs/06-torrent-client.md: allowed, never required. A peer that refuses
/// encryption is still worth having, and a client that gave up on it would
/// throw away half a swarm; a client that never tried encryption would be
/// refused by the other half.
/// </remarks>
/// <summary>
/// What this client will agree to when it dials a peer.
/// </summary>
/// <remarks>
/// The owner's <c>Encryption</c> setting, in the engine's own words. It was on
/// the Settings page and read by nothing at all, so every connection was
/// negotiated the same way whatever the owner had chosen.
/// </remarks>
public enum PeerEncryption
{
    /// <summary>Encrypted when the peer will, in the clear when it will not.</summary>
    Allowed,

    /// <summary>Encrypted or not at all.</summary>
    Required,

    /// <summary>No negotiation: the handshake goes out as it is.</summary>
    Disabled,
}

public static class PeerDial
{
    /// <summary>Connects, agreeing whatever the peer will agree to.</summary>
    /// <param name="connect">
    /// Opens a connection. Called a second time for the fallback: a peer that
    /// would not do MSE has already had ninety-six bytes it made nothing of,
    /// and no real client carries on from there.
    /// </param>
    /// <param name="infoHash">Which torrent.</param>
    /// <param name="handshake">This client's BitTorrent handshake.</param>
    /// <param name="random">Where the key and the padding come from.</param>
    /// <param name="encryption">What the owner will agree to.</param>
    /// <param name="encrypting">
    /// How long the encrypted attempt may take before the clear one is worth
    /// more than finishing it. Ignored where encryption is required, which has
    /// no second attempt to save time for.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<MseLink> ConnectAsync(
        Func<CancellationToken, Task<Stream>> connect,
        byte[] infoHash,
        byte[] handshake,
        RandomNumberGenerator random,
        PeerEncryption encryption,
        TimeSpan encrypting,
        CancellationToken ct)
    {
        if (encryption != PeerEncryption.Disabled)
        {
            // Its own clock. A peer that does not speak MSE does not refuse it:
            // it takes the ninety-six bytes, makes nothing of them, and says
            // nothing back. Without this the whole dial's patience was spent
            // waiting for that silence and the cancellation went straight past
            // the fallback below — so every peer that would have talked in the
            // clear was lost. Measured on 31 August 2026 against the owner's
            // Dark Matter swarm: five of seventeen reachable peers with the
            // fallback unreachable, ten of seventeen without encryption at all.
            using CancellationTokenSource own = new(encrypting);
            using CancellationTokenSource trying = CancellationTokenSource.CreateLinkedTokenSource(ct, own.Token);

            Stream encrypted = await connect(ct).ConfigureAwait(false);

            try
            {
                return await MseNegotiation
                    .InitiateAsync(
                        encrypted,
                        infoHash,
                        handshake,

                        // Required means the payload is encrypted too. Offering
                        // plaintext as well lets a peer agree to the
                        // negotiation and then send everything in the clear,
                        // which is what the owner asked not to happen.
                        encryption == PeerEncryption.Required
                            ? MseMethod.Rc4
                            : MseMethod.Plaintext | MseMethod.Rc4,
                        random,
                        encryption == PeerEncryption.Required ? ct : trying.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception refusal) when (refusal is MseRefusedException or PeerProtocolException or IOException
                                                or EndOfStreamException or OperationCanceledException
                                            && !ct.IsCancellationRequested)
            {
                await encrypted.DisposeAsync().ConfigureAwait(false);

                if (encryption == PeerEncryption.Required)
                {
                    // No second try in the clear. A peer that will not encrypt
                    // is a peer this owner does not talk to.
                    throw;
                }
            }
        }

        Stream plain = await connect(ct).ConfigureAwait(false);

        await plain.WriteAsync(handshake, ct).ConfigureAwait(false);
        await plain.FlushAsync(ct).ConfigureAwait(false);

        return new(plain, MseMethod.Plaintext, infoHash, []);
    }
}
