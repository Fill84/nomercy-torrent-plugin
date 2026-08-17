using System.Numerics;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>Which of the two a connection ended up using.</summary>
[Flags]
public enum MseMethod
{
    /// <summary>Nothing agreed.</summary>
    None = 0,

    /// <summary>The wire in the clear, after the key exchange.</summary>
    Plaintext = 1,

    /// <summary>RC4 both ways.</summary>
    Rc4 = 2,
}

/// <summary>
/// MSE/PE: the obfuscation two peers agree before either says a word about a
/// torrent.
/// </summary>
/// <remarks>
/// <para>
/// It is not security and is not meant to be — RC4 with a key both ends
/// published half of stops a router recognising BitTorrent, and nothing more.
/// What it buys is peers: a great many refuse a connection that arrives in the
/// clear, which is the likeliest reason almost nobody answered this client
/// during <c>S5-05</c>.
/// </para>
/// <para>
/// Allowed, never required, from docs/06-torrent-client.md: a peer that will
/// not do it is still used in plaintext.
/// </para>
/// </remarks>
public static class Mse
{
    /// <summary>
    /// The 768-bit prime MSE names, which is RFC 2409's first Oakley group.
    /// </summary>
    /// <remarks>
    /// Every peer in the world uses this one. A digit typed wrong here gives a
    /// shared secret nobody else can arrive at, and this client would then talk
    /// happily to itself and to nothing else.
    /// </remarks>
    public static ReadOnlySpan<byte> Prime =>
    [
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xC9, 0x0F, 0xDA, 0xA2,
        0x21, 0x68, 0xC2, 0x34, 0xC4, 0xC6, 0x62, 0x8B, 0x80, 0xDC, 0x1C, 0xD1,
        0x29, 0x02, 0x4E, 0x08, 0x8A, 0x67, 0xCC, 0x74, 0x02, 0x0B, 0xBE, 0xA6,
        0x3B, 0x13, 0x9B, 0x22, 0x51, 0x4A, 0x08, 0x79, 0x8E, 0x34, 0x04, 0xDD,
        0xEF, 0x95, 0x19, 0xB3, 0xCD, 0x3A, 0x43, 0x1B, 0x30, 0x2B, 0x0A, 0x6D,
        0xF2, 0x5F, 0x14, 0x37, 0x4F, 0xE1, 0x35, 0x6D, 0x6D, 0x51, 0xC2, 0x45,
        0xE4, 0x85, 0xB5, 0x76, 0x62, 0x5E, 0x7E, 0xC6, 0xF4, 0x4C, 0x42, 0xE9,
        0xA6, 0x3A, 0x36, 0x20, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,
    ];

    /// <summary>The generator, which is two.</summary>
    public const int Generator = 2;

    /// <summary>How long a public key is: 768 bits, always, left-padded.</summary>
    public const int KeyLength = 96;

    /// <summary>How many bits of private key. MSE says at least 160.</summary>
    public const int PrivateBits = 160;

    /// <summary>
    /// How much of the RC4 keystream is thrown away before anything is sent.
    /// </summary>
    /// <remarks>
    /// RC4's first bytes are biased towards the key, which is what broke it in
    /// WEP. A thousand and twenty-four bytes go in the bin at both ends, and a
    /// peer that skipped them reads rubbish from the first byte on.
    /// </remarks>
    public const int Discard = 1024;

    /// <summary>The most padding either side may send, at each of the four places.</summary>
    public const int MostPadding = 512;

    /// <summary>
    /// The eight bytes of nought that say the secret was agreed.
    /// </summary>
    /// <remarks>
    /// Sent encrypted. Finding it decrypted at the other end is the only proof
    /// both sides arrived at the same secret, and looking for it is how the
    /// receiver finds where the padding stopped.
    /// </remarks>
    public static ReadOnlySpan<byte> Verification => [0, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>SHA-1 over these, in order.</summary>
    public static byte[] Hash(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second, ReadOnlySpan<byte> third = default)
    {
        byte[] joined = new byte[first.Length + second.Length + third.Length];

        first.CopyTo(joined);
        second.CopyTo(joined.AsSpan(first.Length));
        third.CopyTo(joined.AsSpan(first.Length + second.Length));

        return SHA1.HashData(joined);
    }

    /// <summary>What the initiator sends so a receiver can find where the padding ended.</summary>
    public static byte[] Req1(ReadOnlySpan<byte> secret)
    {
        return Hash("req1"u8, secret);
    }

    /// <summary>
    /// Which torrent, in a form only somebody who already has the hash can read.
    /// </summary>
    /// <remarks>
    /// The info hash never goes over the wire, obfuscated or otherwise: an
    /// observer holding a list of hashes could otherwise say which torrent this
    /// connection is for. Exclusive-or with something derived from the shared
    /// secret means only a peer that did the key exchange <em>and</em> already
    /// knows the hash can recognise it.
    /// </remarks>
    public static byte[] Req2Xor3(ReadOnlySpan<byte> infoHash, ReadOnlySpan<byte> secret)
    {
        byte[] req2 = Hash("req2"u8, infoHash);
        byte[] req3 = Hash("req3"u8, secret);

        for (int at = 0; at < req2.Length; at++)
        {
            req2[at] ^= req3[at];
        }

        return req2;
    }

    /// <summary>The key the initiator writes with and the receiver reads with.</summary>
    public static byte[] KeyA(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> infoHash)
    {
        return Hash("keyA"u8, secret, infoHash);
    }

    /// <summary>The other direction's, which is a different key on purpose.</summary>
    public static byte[] KeyB(ReadOnlySpan<byte> secret, ReadOnlySpan<byte> infoHash)
    {
        return Hash("keyB"u8, secret, infoHash);
    }
}

/// <summary>
/// One end's Diffie-Hellman key.
/// </summary>
/// <remarks>
/// The private half never leaves this object. The public half is the number
/// left-padded to ninety-six bytes: a client that sent it without the padding
/// would send a shorter key one time in about two hundred and fifty, and that
/// connection would fail for no reason anybody could see.
/// </remarks>
public sealed class MseKeyPair
{
    private readonly BigInteger _private;

    private MseKeyPair(BigInteger secret)
    {
        _private = secret;

        Public = Number(BigInteger.ModPow(Mse.Generator, secret, PrimeOf()));
    }

    /// <summary>Ninety-six bytes to send.</summary>
    public byte[] Public { get; }

    /// <summary>A fresh key.</summary>
    /// <param name="random">Where the private half comes from, so a test can hand in a known one.</param>
    public static MseKeyPair Create(RandomNumberGenerator random)
    {
        byte[] bytes = new byte[Mse.PrivateBits / 8];

        random.GetBytes(bytes);

        // Unsigned, whatever the top bit says: a negative exponent is not a
        // Diffie-Hellman private key.
        return new(new BigInteger(bytes, isUnsigned: true, isBigEndian: true));
    }

    /// <summary>The shared secret, from the other side's public key.</summary>
    public byte[] Secret(ReadOnlySpan<byte> theirs)
    {
        if (theirs.Length != Mse.KeyLength)
        {
            throw new PeerProtocolException($"A public key is {Mse.KeyLength} bytes, and this one is {theirs.Length}.");
        }

        return Number(BigInteger.ModPow(
            new BigInteger(theirs, isUnsigned: true, isBigEndian: true),
            _private,
            PrimeOf()));
    }

    private static BigInteger PrimeOf()
    {
        return new(Mse.Prime, isUnsigned: true, isBigEndian: true);
    }

    /// <summary>A number as ninety-six bytes, big-endian, left-padded with nought.</summary>
    private static byte[] Number(BigInteger value)
    {
        byte[] bytes = new byte[Mse.KeyLength];

        value.TryWriteBytes(bytes, out int written, isUnsigned: true, isBigEndian: true);

        if (written < bytes.Length)
        {
            // Written to the front by BigInteger, and it belongs at the back.
            Array.Copy(bytes, 0, bytes, bytes.Length - written, written);
            Array.Clear(bytes, 0, bytes.Length - written);
        }

        return bytes;
    }
}

/// <summary>
/// RC4, with MSE's thousand-and-twenty-four discarded bytes.
/// </summary>
/// <remarks>
/// Written here because .NET has no RC4 and will not be getting one: it is
/// broken as a cipher, and MSE is not using it as one.
/// </remarks>
public sealed class Rc4
{
    private readonly byte[] _state = new byte[256];
    private int _i;
    private int _j;

    public Rc4(ReadOnlySpan<byte> key, int discard = Mse.Discard)
    {
        for (int at = 0; at < _state.Length; at++)
        {
            _state[at] = (byte)at;
        }

        int mix = 0;

        for (int at = 0; at < _state.Length; at++)
        {
            mix = (mix + _state[at] + key[at % key.Length]) & 0xFF;

            (_state[at], _state[mix]) = (_state[mix], _state[at]);
        }

        for (int at = 0; at < discard; at++)
        {
            Next();
        }
    }

    /// <summary>Encrypts or decrypts in place; with a stream cipher they are the same thing.</summary>
    public void Apply(Span<byte> bytes)
    {
        for (int at = 0; at < bytes.Length; at++)
        {
            bytes[at] ^= Next();
        }
    }

    private byte Next()
    {
        _i = (_i + 1) & 0xFF;
        _j = (_j + _state[_i]) & 0xFF;

        (_state[_i], _state[_j]) = (_state[_j], _state[_i]);

        return _state[(_state[_i] + _state[_j]) & 0xFF];
    }
}
