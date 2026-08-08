// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;

/// <summary>
/// The Diffie-Hellman exchange and key derivation MSE defines.
///
/// <para>
/// Every constant here is fixed by the MSE specification. They are not tuning knobs:
/// a peer that computes a different prime, a different generator, or a different key
/// label derives a different secret, and the connection dies at the first encrypted
/// byte with no diagnostic beyond silence.
/// </para>
/// </summary>
public static class MseCrypto
{
    public const int KeyLength = 96;

    /// <summary>The 768-bit prime MSE specifies. Not Oakley group 1 - MSE uses its own tail.</summary>
    private static readonly BigInteger P = Parse(
        "FFFFFFFFFFFFFFFFC90FDAA22168C234C4C6628B80DC1CD129024E088A67CC74" +
        "020BBEA63B139B22514A08798E3404DDEF9519B3CD3A431B302B0A6DF25F1437" +
        "4FE1356D6D51C245E485B576625E7EC6F44C42E9A63A36210000000000090563");

    private static readonly BigInteger G = 2;

    /// <summary>160 bits, as the specification asks. Bigger buys nothing against this prime.</summary>
    public static byte[] NewPrivateKey()
    {
        byte[] key = new byte[20];
        RandomNumberGenerator.Fill(key);
        return key;
    }

    public static byte[] PublicKey(ReadOnlySpan<byte> privateKey) =>
        ToFixedWidth(BigInteger.ModPow(G, ToPositive(privateKey), P));

    public static byte[] SharedSecret(ReadOnlySpan<byte> privateKey, ReadOnlySpan<byte> remotePublicKey)
    {
        if (remotePublicKey.Length != KeyLength)
            throw new ArgumentException($"a public key is {KeyLength} bytes, not {remotePublicKey.Length}", nameof(remotePublicKey));

        return ToFixedWidth(BigInteger.ModPow(ToPositive(remotePublicKey), ToPositive(privateKey), P));
    }

    /// <summary>SHA-1 over an ASCII label followed by each part, which is how every MSE hash is built.</summary>
    public static byte[] Hash(string label, params byte[][] parts)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

        hash.AppendData(System.Text.Encoding.ASCII.GetBytes(label));

        foreach (byte[] part in parts)
            hash.AppendData(part);

        return hash.GetHashAndReset();
    }

    /// <summary>
    /// The stream key for one direction. The side that dialled writes with "keyA" and
    /// reads with "keyB"; the side that answered does the opposite. One key for both
    /// directions would let a captured message be replayed back as its own answer.
    /// </summary>
    public static byte[] Key(bool initiating, byte[] sharedSecret, byte[] infoHash) =>
        Hash(initiating ? "keyA" : "keyB", sharedSecret, infoHash);

    /// <summary>
    /// The info hash as it goes on the wire: HASH('req2', SKEY) xor HASH('req3', S).
    /// A listener that does not know the secret cannot tell which torrent this is.
    /// </summary>
    public static byte[] ObfuscatedInfoHash(byte[] sharedSecret, byte[] infoHash)
    {
        byte[] fromTorrent = Hash("req2", infoHash);
        byte[] fromSecret = Hash("req3", sharedSecret);
        byte[] combined = new byte[fromTorrent.Length];

        for (int index = 0; index < combined.Length; index++)
            combined[index] = (byte)(fromTorrent[index] ^ fromSecret[index]);

        return combined;
    }

    private static BigInteger Parse(string hex) =>
        BigInteger.Parse("0" + hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);

    /// <summary>
    /// BigInteger reads bytes as two's complement, so a value whose top bit is set would
    /// come back negative. The wire carries an unsigned big-endian number.
    /// </summary>
    private static BigInteger ToPositive(ReadOnlySpan<byte> bigEndian) =>
        new(bigEndian, isUnsigned: true, isBigEndian: true);

    /// <summary>
    /// Always <see cref="KeyLength"/> bytes, left-padded. The handshake has no length
    /// prefix, so a key one byte short shifts everything that follows it.
    /// </summary>
    private static byte[] ToFixedWidth(BigInteger value)
    {
        byte[] fixedWidth = new byte[KeyLength];

        // Every value here is a residue mod P and so fits in 96 bytes. Checking anyway:
        // ignoring the result would turn an impossible case into a key of all zeroes,
        // which would look like a working handshake right up until nothing decrypts.
        if (!value.TryWriteBytes(fixedWidth, out int written, isUnsigned: true, isBigEndian: true))
            throw new InvalidOperationException($"a {value.GetByteCount(isUnsigned: true)} byte value does not fit a {KeyLength} byte key");

        if (written == KeyLength)
            return fixedWidth;

        byte[] padded = new byte[KeyLength];
        fixedWidth.AsSpan(0, written).CopyTo(padded.AsSpan(KeyLength - written));
        return padded;
    }
}
