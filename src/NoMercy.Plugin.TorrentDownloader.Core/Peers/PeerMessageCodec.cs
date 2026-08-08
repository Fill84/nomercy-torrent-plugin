// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers;

/// <summary>
/// The peer wire format: a four-byte big-endian length, then an id, then a body.
/// Reading takes a <see cref="Stream"/> rather than a socket, which is what lets both
/// ends of a connection be driven in one process by a test.
/// </summary>
public static class PeerMessageCodec
{
    /// <summary>
    /// A block is 16 KiB by convention and no peer needs to send more in one message.
    /// The cap is generous enough for any real client and small enough that a peer
    /// cannot make us reserve memory on command.
    /// </summary>
    public const int MaxMessageLength = 1 << 20;

    private const byte IdChoke = 0;
    private const byte IdUnchoke = 1;
    private const byte IdInterested = 2;
    private const byte IdNotInterested = 3;
    private const byte IdHave = 4;
    private const byte IdBitfield = 5;
    private const byte IdRequest = 6;
    private const byte IdPiece = 7;
    private const byte IdCancel = 8;
    private const byte IdPort = 9;
    private const byte IdExtended = 20;

    public static byte[] Write(PeerMessage message)
    {
        byte[] body = Body(message);
        byte[] framed = new byte[4 + body.Length];

        BinaryPrimitives.WriteInt32BigEndian(framed, body.Length);
        body.CopyTo(framed, 4);

        return framed;
    }

    public static async ValueTask<PeerMessage> ReadAsync(Stream stream, CancellationToken ct)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);

        int length = BinaryPrimitives.ReadInt32BigEndian(header);

        if (length == 0)
            return new KeepAlive();

        if (length < 0 || length > MaxMessageLength)
            throw new PeerProtocolException($"a peer announced a {length} byte message");

        byte[] body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);

        return Parse(body);
    }

    private static byte[] Body(PeerMessage message) => message switch
    {
        KeepAlive => [],
        Choke => [IdChoke],
        Unchoke => [IdUnchoke],
        Interested => [IdInterested],
        NotInterested => [IdNotInterested],
        Have have => [IdHave, .. BigEndian(have.PieceIndex)],
        BitfieldMessage bitfield => [IdBitfield, .. bitfield.Payload],
        Request request => [IdRequest, .. BigEndian(request.PieceIndex), .. BigEndian(request.Begin), .. BigEndian(request.Length)],
        PieceBlock piece => [IdPiece, .. BigEndian(piece.PieceIndex), .. BigEndian(piece.Begin), .. piece.Block],
        Cancel cancel => [IdCancel, .. BigEndian(cancel.PieceIndex), .. BigEndian(cancel.Begin), .. BigEndian(cancel.Length)],
        Port port => [IdPort, (byte)(port.Listen >> 8), (byte)port.Listen],
        Extended extended => [IdExtended, extended.ExtensionId, .. extended.Payload],
        _ => throw new PeerProtocolException($"cannot write {message.GetType().Name}"),
    };

    private static PeerMessage Parse(ReadOnlySpan<byte> body)
    {
        byte id = body[0];
        ReadOnlySpan<byte> payload = body[1..];

        return id switch
        {
            IdChoke => Empty<Choke>(payload, id, new Choke()),
            IdUnchoke => Empty<Unchoke>(payload, id, new Unchoke()),
            IdInterested => Empty<Interested>(payload, id, new Interested()),
            IdNotInterested => Empty<NotInterested>(payload, id, new NotInterested()),
            IdHave => payload.Length == 4
                ? new Have(BinaryPrimitives.ReadInt32BigEndian(payload))
                : throw Malformed(id, payload.Length),
            IdBitfield => new BitfieldMessage(payload.ToArray()),
            IdRequest => payload.Length == 12
                ? new Request(
                    BinaryPrimitives.ReadInt32BigEndian(payload),
                    BinaryPrimitives.ReadInt32BigEndian(payload[4..]),
                    BinaryPrimitives.ReadInt32BigEndian(payload[8..]))
                : throw Malformed(id, payload.Length),
            IdPiece => payload.Length >= 8
                ? new PieceBlock(
                    BinaryPrimitives.ReadInt32BigEndian(payload),
                    BinaryPrimitives.ReadInt32BigEndian(payload[4..]),
                    payload[8..].ToArray())
                : throw Malformed(id, payload.Length),
            IdCancel => payload.Length == 12
                ? new Cancel(
                    BinaryPrimitives.ReadInt32BigEndian(payload),
                    BinaryPrimitives.ReadInt32BigEndian(payload[4..]),
                    BinaryPrimitives.ReadInt32BigEndian(payload[8..]))
                : throw Malformed(id, payload.Length),
            IdPort => payload.Length == 2
                ? new Port((payload[0] << 8) | payload[1])
                : throw Malformed(id, payload.Length),
            IdExtended => payload.Length >= 1
                ? new Extended(payload[0], payload[1..].ToArray())
                : throw Malformed(id, payload.Length),
            _ => throw new PeerProtocolException($"unknown message id {id}"),
        };
    }

    private static PeerMessage Empty<T>(ReadOnlySpan<byte> payload, byte id, PeerMessage message) =>
        payload.IsEmpty ? message : throw Malformed(id, payload.Length);

    private static PeerProtocolException Malformed(byte id, int payloadLength) =>
        new($"message id {id} cannot carry a {payloadLength} byte payload");

    private static byte[] BigEndian(int value)
    {
        byte[] bytes = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(bytes, value);
        return bytes;
    }
}
