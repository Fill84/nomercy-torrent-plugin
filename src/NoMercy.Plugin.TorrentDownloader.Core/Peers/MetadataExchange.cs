// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers;

/// <summary>
/// BEP 10. Each side names the extensions it speaks and the number it wants them
/// addressed by. The numbers are per connection, so a peer's choice must be read
/// and used rather than assumed.
/// </summary>
public sealed record ExtensionHandshake(int? UtMetadataId, int? MetadataSize)
{
    /// <summary>The id we ask peers to use when sending us ut_metadata. Any number but zero would do.</summary>
    public const int OurUtMetadataId = 1;

    public static byte[] Write(int? metadataSize)
    {
        Dictionary<string, BValue> handshake = new()
        {
            ["m"] = new BDictionary(new Dictionary<string, BValue>
            {
                ["ut_metadata"] = new BInteger(OurUtMetadataId),
            }),
        };

        // Only claimed once we actually have the metadata. Announcing a size we cannot
        // serve invites requests we would have to reject.
        if (metadataSize is int size)
            handshake["metadata_size"] = new BInteger(size);

        return BencodeWriter.Write(new BDictionary(handshake));
    }

    public static ExtensionHandshake Parse(ReadOnlySpan<byte> payload)
    {
        if (BencodeReader.Parse(payload) is not BDictionary handshake)
            throw new PeerProtocolException("an extension handshake must be a dictionary");

        int? utMetadata = null;

        if (handshake.Entries.TryGetValue("m", out BValue? supported) && supported is BDictionary map
            && map.Entries.TryGetValue("ut_metadata", out BValue? id) && id is BInteger number && number.Value > 0)
        {
            utMetadata = (int)number.Value;
        }

        int? size = handshake.Entries.TryGetValue("metadata_size", out BValue? declared) && declared is BInteger length
            ? (int)length.Value
            : null;

        return new ExtensionHandshake(utMetadata, size);
    }
}

public enum MetadataMessageType
{
    Request = 0,
    Data = 1,
    Reject = 2,
}

/// <summary>
/// BEP 9. A bencoded dictionary, and for a data message the raw bytes immediately
/// after it - the dictionary carries no length, so the payload is whatever remains.
/// </summary>
public sealed record MetadataMessage(MetadataMessageType Type, int Piece, int? TotalSize, byte[] Data)
{
    public static byte[] WriteRequest(int piece) => Header(MetadataMessageType.Request, piece, null);

    public static byte[] WriteReject(int piece) => Header(MetadataMessageType.Reject, piece, null);

    public static byte[] WriteData(int piece, int totalSize, byte[] data) =>
        [.. Header(MetadataMessageType.Data, piece, totalSize), .. data];

    public static MetadataMessage Parse(ReadOnlySpan<byte> payload)
    {
        BValue parsed = BencodeReader.Read(payload, out int consumed);

        if (parsed is not BDictionary message)
            throw new PeerProtocolException("a ut_metadata message must start with a dictionary");

        int type = Integer(message, "msg_type");
        int piece = Integer(message, "piece");

        int? totalSize = message.Entries.TryGetValue("total_size", out BValue? size) && size is BInteger number
            ? (int)number.Value
            : null;

        return new MetadataMessage((MetadataMessageType)type, piece, totalSize, payload[consumed..].ToArray());
    }

    private static byte[] Header(MetadataMessageType type, int piece, int? totalSize)
    {
        Dictionary<string, BValue> header = new()
        {
            ["msg_type"] = new BInteger((int)type),
            ["piece"] = new BInteger(piece),
        };

        if (totalSize is int size)
            header["total_size"] = new BInteger(size);

        return BencodeWriter.Write(new BDictionary(header));
    }

    private static int Integer(BDictionary message, string key) =>
        message.Entries.TryGetValue(key, out BValue? value) && value is BInteger number
            ? (int)number.Value
            : throw new PeerProtocolException($"a ut_metadata message needs '{key}'");
}

/// <summary>
/// Rebuilding a torrent from strangers.
///
/// <para>
/// A magnet names an info hash and nothing else, so the info dictionary is fetched in
/// pieces from the peers themselves. Nothing is trusted until the assembled bytes hash
/// to the info hash the magnet named - without that check any peer could hand us a
/// different torrent and we would download it.
/// </para>
/// </summary>
public sealed class MetadataDownload
{
    /// <summary>Metadata travels in 16 KiB pieces, like everything else on this wire.</summary>
    public const int PieceLength = 16 * 1024;

    /// <summary>
    /// No real torrent's info dictionary approaches this. The cap is here so a peer
    /// cannot name a size that makes us allocate on command.
    /// </summary>
    public const int MaxMetadataSize = 8 * 1024 * 1024;

    private readonly byte[] _expectedInfoHash;
    private readonly byte[] _buffer;
    private readonly bool[] _received;

    public MetadataDownload(byte[] expectedInfoHash, int totalSize)
    {
        if (totalSize <= 0 || totalSize > MaxMetadataSize)
            throw new MetadataException($"{totalSize} is not a plausible size for a torrent's metadata");

        _expectedInfoHash = expectedInfoHash;
        _buffer = new byte[totalSize];
        _received = new bool[(totalSize + PieceLength - 1) / PieceLength];
    }

    public int TotalSize => _buffer.Length;

    public int PieceCount => _received.Length;

    public bool IsComplete => _received.All(received => received);

    public IEnumerable<int> MissingPieces()
    {
        for (int index = 0; index < _received.Length; index++)
        {
            if (!_received[index])
                yield return index;
        }
    }

    /// <summary>False for anything that does not fit, which is a peer to stop asking.</summary>
    public bool Accept(int piece, byte[] data)
    {
        if (piece < 0 || piece >= _received.Length)
            return false;

        int offset = piece * PieceLength;
        int expected = Math.Min(PieceLength, _buffer.Length - offset);

        if (data.Length != expected)
            return false;

        data.CopyTo(_buffer, offset);
        _received[piece] = true;

        return true;
    }

    public TorrentMetadata Build(IReadOnlyList<string> trackers)
    {
        if (!IsComplete)
            throw new MetadataException("the metadata is not complete yet");

        if (!SHA1.HashData(_buffer).AsSpan().SequenceEqual(_expectedInfoHash))
            throw new MetadataException("the metadata does not hash to the info hash the magnet named");

        if (BencodeReader.Parse(_buffer) is not BDictionary info)
            throw new MetadataException("the metadata is not a dictionary");

        return MetadataParser.FromInfoDictionary(info, trackers);
    }
}
