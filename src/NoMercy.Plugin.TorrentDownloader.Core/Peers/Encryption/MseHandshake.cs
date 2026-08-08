// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using System.Security.Cryptography;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;

/// <summary>What the answering side learned while agreeing on a tunnel.</summary>
public sealed record MseAccepted(Stream Stream, byte[] InitialPayload);

/// <summary>
/// Message Stream Encryption, forced.
///
/// <para>
/// Both sides exchange Diffie-Hellman keys behind random padding, prove they know the
/// same torrent without naming it, and agree on RC4. This plugin only ever offers RC4:
/// a peer that selects plaintext is refused rather than downgraded to, because half
/// the value of turning encryption on is that the other side cannot turn it off.
/// </para>
/// </summary>
public static class MseHandshake
{
    private const int MaxPad = 512;
    private const int VerificationLength = 8;
    private const int CryptoPlaintext = 0x01;
    private const int CryptoRc4 = 0x02;

    /// <summary>Generous enough for the other side's padding, small enough to bound the scan.</summary>
    private const int MaxSyncScan = MaxPad + 96 + 20;

    public static async Task<Stream> InitiateAsync(
        Stream raw,
        byte[] infoHash,
        ReadOnlyMemory<byte> initialPayload,
        CancellationToken ct)
    {
        byte[] ourPrivate = MseCrypto.NewPrivateKey();

        await raw.WriteAsync(MseCrypto.PublicKey(ourPrivate), ct);
        await raw.WriteAsync(RandomPad(), ct);
        await raw.FlushAsync(ct);

        byte[] theirPublic = new byte[MseCrypto.KeyLength];
        await raw.ReadExactlyAsync(theirPublic, ct);

        byte[] secret = MseCrypto.SharedSecret(ourPrivate, theirPublic);
        byte[] writeKey = MseCrypto.Key(initiating: true, secret, infoHash);
        byte[] readKey = MseCrypto.Key(initiating: false, secret, infoHash);

        Rc4Engine encryptor = new(writeKey, discardBytes: 1024);
        Rc4Engine decryptor = new(readKey, discardBytes: 1024);

        // Two plain markers, then everything else enciphered. The first proves we know
        // the secret; the second names the torrent in a form only a peer holding the
        // same secret can recognise.
        await raw.WriteAsync(MseCrypto.Hash("req1", secret), ct);
        await raw.WriteAsync(MseCrypto.ObfuscatedInfoHash(secret, infoHash), ct);

        byte[] request =
        [
            .. new byte[VerificationLength],
            .. BigEndian(CryptoRc4),
            .. TwoBytes(0),
            .. TwoBytes(initialPayload.Length),
            .. initialPayload.ToArray(),
        ];

        encryptor.Process(request);
        await raw.WriteAsync(request, ct);
        await raw.FlushAsync(ct);

        // Their padding is of unknown length and carries no marker, so we scan for the
        // first thing they encipher: the verification constant. Its ciphertext is the
        // opening bytes of their keystream, which we can compute without reading anything.
        byte[] expected = new byte[VerificationLength];
        new Rc4Engine(readKey, discardBytes: 1024).Process(expected);

        if (!await SyncAsync(raw, expected, MaxSyncScan, ct))
            throw new PeerProtocolException("the peer never sent a recognisable encrypted reply");

        // The bytes we matched were theirs to encipher, so our decryptor has to move past
        // them or every byte after this is deciphered against the wrong keystream position.
        decryptor.Process(new byte[VerificationLength]);

        byte[] selection = new byte[4];
        await ReadDecryptedAsync(raw, decryptor, selection, ct);

        if ((BinaryPrimitives.ReadInt32BigEndian(selection) & CryptoRc4) == 0)
            throw new PeerProtocolException("the peer refused RC4 and this plugin does not fall back to plaintext");

        await SkipPaddingAsync(raw, decryptor, ct);

        return new MseStream(raw, encryptor, decryptor);
    }

    public static async Task<MseAccepted> AcceptAsync(
        Stream raw,
        byte[] infoHash,
        CancellationToken ct,
        bool forcePlaintextForTest = false)
    {
        byte[] theirPublic = new byte[MseCrypto.KeyLength];
        await raw.ReadExactlyAsync(theirPublic, ct);

        byte[] ourPrivate = MseCrypto.NewPrivateKey();
        await raw.WriteAsync(MseCrypto.PublicKey(ourPrivate), ct);
        await raw.WriteAsync(RandomPad(), ct);
        await raw.FlushAsync(ct);

        byte[] secret = MseCrypto.SharedSecret(ourPrivate, theirPublic);

        if (!await SyncAsync(raw, MseCrypto.Hash("req1", secret), MaxSyncScan, ct))
            throw new PeerProtocolException("the peer never proved it knows the shared secret");

        byte[] claimed = new byte[20];
        await raw.ReadExactlyAsync(claimed, ct);

        // Constant-time: this is the one comparison an attacker could otherwise probe
        // byte by byte to learn which torrents this server holds.
        if (!CryptographicOperations.FixedTimeEquals(claimed, MseCrypto.ObfuscatedInfoHash(secret, infoHash)))
            throw new PeerProtocolException("the peer asked for a different torrent");

        byte[] readKey = MseCrypto.Key(initiating: true, secret, infoHash);
        byte[] writeKey = MseCrypto.Key(initiating: false, secret, infoHash);

        Rc4Engine decryptor = new(readKey, discardBytes: 1024);
        Rc4Engine encryptor = new(writeKey, discardBytes: 1024);

        byte[] verification = new byte[VerificationLength];
        await ReadDecryptedAsync(raw, decryptor, verification, ct);

        if (verification.Any(value => value != 0))
            throw new PeerProtocolException("the verification constant did not decrypt to zeroes");

        byte[] provided = new byte[4];
        await ReadDecryptedAsync(raw, decryptor, provided, ct);

        int offered = BinaryPrimitives.ReadInt32BigEndian(provided);

        if ((offered & CryptoRc4) == 0)
            throw new PeerProtocolException("the peer did not offer RC4");

        await SkipPaddingAsync(raw, decryptor, ct);

        byte[] payloadLength = new byte[2];
        await ReadDecryptedAsync(raw, decryptor, payloadLength, ct);

        byte[] initialPayload = new byte[BinaryPrimitives.ReadUInt16BigEndian(payloadLength)];

        if (initialPayload.Length > 0)
            await ReadDecryptedAsync(raw, decryptor, initialPayload, ct);

        byte[] reply =
        [
            .. new byte[VerificationLength],
            .. BigEndian(forcePlaintextForTest ? CryptoPlaintext : CryptoRc4),
            .. TwoBytes(0),
        ];

        encryptor.Process(reply);
        await raw.WriteAsync(reply, ct);
        await raw.FlushAsync(ct);

        return new MseAccepted(new MseStream(raw, encryptor, decryptor), initialPayload);
    }

    private static async Task SkipPaddingAsync(Stream raw, Rc4Engine decryptor, CancellationToken ct)
    {
        byte[] length = new byte[2];
        await ReadDecryptedAsync(raw, decryptor, length, ct);

        int padding = BinaryPrimitives.ReadUInt16BigEndian(length);

        if (padding > MaxPad)
            throw new PeerProtocolException($"the peer announced {padding} bytes of padding");

        if (padding == 0)
            return;

        await ReadDecryptedAsync(raw, decryptor, new byte[padding], ct);
    }

    private static async Task ReadDecryptedAsync(Stream raw, Rc4Engine decryptor, byte[] buffer, CancellationToken ct)
    {
        await raw.ReadExactlyAsync(buffer, ct);
        decryptor.Process(buffer);
    }

    /// <summary>
    /// Reads forward until the marker has just been consumed. Padding is random and
    /// unannounced, so finding where the real message starts means looking for it.
    /// </summary>
    private static async Task<bool> SyncAsync(Stream raw, byte[] marker, int maxBytes, CancellationToken ct)
    {
        byte[] window = new byte[marker.Length];
        byte[] one = new byte[1];
        int filled = 0;

        for (int scanned = 0; scanned < maxBytes + marker.Length; scanned++)
        {
            await raw.ReadExactlyAsync(one, ct);

            if (filled < window.Length)
            {
                window[filled++] = one[0];
            }
            else
            {
                Array.Copy(window, 1, window, 0, window.Length - 1);
                window[^1] = one[0];
            }

            if (filled == window.Length && window.AsSpan().SequenceEqual(marker))
                return true;
        }

        return false;
    }

    private static byte[] RandomPad()
    {
        byte[] length = new byte[1];
        RandomNumberGenerator.Fill(length);

        byte[] pad = new byte[length[0] * MaxPad / 256];
        RandomNumberGenerator.Fill(pad);

        return pad;
    }

    private static byte[] BigEndian(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] TwoBytes(int value)
    {
        byte[] bytes = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(bytes, (ushort)value);
        return bytes;
    }
}
