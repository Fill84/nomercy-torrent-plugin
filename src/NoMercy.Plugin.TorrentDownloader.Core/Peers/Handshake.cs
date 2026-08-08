// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers;

/// <summary>
/// The 68 bytes both ends send before anything else: a length, the protocol name,
/// eight reserved bytes, the info hash, and a peer id.
/// </summary>
public sealed record Handshake(byte[] InfoHash, byte[] PeerId, byte[] Reserved)
{
    private const int ProtocolLength = 19;
    private const int Length = 68;
    private const int HashLength = 20;

    private static readonly byte[] ProtocolName = "BitTorrent protocol"u8.ToArray();

    /// <summary>Azureus-style client tag: two letters, four digits of version.</summary>
    private static readonly byte[] ClientTag = "-NM0100-"u8.ToArray();

    /// <summary>True when the peer speaks BEP 10, which part two needs for magnet metadata.</summary>
    public bool SupportsExtensionProtocol => (Reserved[5] & 0x10) != 0;

    public static byte[] NewPeerId()
    {
        byte[] id = new byte[HashLength];
        ClientTag.CopyTo(id, 0);

        // Random rather than a counter: a peer id that repeats across restarts lets a
        // tracker or peer correlate sessions we have no reason to link.
        RandomNumberGenerator.Fill(id.AsSpan(ClientTag.Length));

        // Keep it printable so a remote peer's logs stay readable.
        for (int index = ClientTag.Length; index < id.Length; index++)
            id[index] = (byte)('a' + (id[index] % 26));

        return id;
    }

    public static byte[] Write(ReadOnlySpan<byte> infoHash, ReadOnlySpan<byte> peerId)
    {
        if (infoHash.Length != HashLength)
            throw new ArgumentException($"an info hash is {HashLength} bytes, not {infoHash.Length}", nameof(infoHash));

        if (peerId.Length != HashLength)
            throw new ArgumentException($"a peer id is {HashLength} bytes, not {peerId.Length}", nameof(peerId));

        byte[] handshake = new byte[Length];

        handshake[0] = ProtocolLength;
        ProtocolName.CopyTo(handshake, 1);

        // Reserved bytes are zero except bit 20 from the left, which claims BEP 10.
        handshake[25] = 0x10;

        infoHash.CopyTo(handshake.AsSpan(28));
        peerId.CopyTo(handshake.AsSpan(48));

        return handshake;
    }

    public static async ValueTask<Handshake> ReadAsync(Stream stream, byte[] expectedInfoHash, CancellationToken ct)
    {
        byte[] buffer = new byte[Length];
        await stream.ReadExactlyAsync(buffer, ct);

        if (buffer[0] != ProtocolLength)
            throw new PeerProtocolException($"the handshake announced a {buffer[0]} byte protocol name");

        if (!buffer.AsSpan(1, ProtocolLength).SequenceEqual(ProtocolName))
            throw new PeerProtocolException($"'{Encoding.ASCII.GetString(buffer, 1, ProtocolLength)}' is not the BitTorrent protocol");

        byte[] infoHash = buffer[28..48];

        // A peer answering with a different info hash is talking about another torrent.
        // Continuing would mean writing its pieces into our files.
        if (!infoHash.AsSpan().SequenceEqual(expectedInfoHash))
            throw new PeerProtocolException("the handshake carried a different info hash");

        return new Handshake(infoHash, buffer[48..68], buffer[20..28]);
    }
}
