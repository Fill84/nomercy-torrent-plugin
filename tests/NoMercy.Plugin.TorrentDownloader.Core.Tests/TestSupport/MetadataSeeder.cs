// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// A peer that hands over a torrent's metadata, and can be told to lie about it.
///
/// <para>
/// Resolving a magnet means trusting strangers for the file list, so the test that
/// matters most is the one where a stranger sends something else. This is how that
/// gets provoked.
/// </para>
/// </summary>
public sealed class MetadataSeeder(byte[] torrentFile)
{
    private const int ExtensionHandshakeId = 0;

    /// <summary>The id this peer asks us to address its ut_metadata messages to. Deliberately not ours.</summary>
    public int OurExtensionId { get; init; } = 7;

    /// <summary>Serve a different torrent's info dictionary, which must be caught by the hash check.</summary>
    public byte[]? LieWith { get; init; }

    /// <summary>Refuse every piece, the way a peer that does not have the metadata does.</summary>
    public bool RejectEverything { get; init; }

    /// <summary>Claim not to speak the extension protocol at all.</summary>
    public bool SupportsNothing { get; init; }

    public async Task ServeAsync(Stream raw, TorrentMetadata metadata, CancellationToken ct)
    {
        await using PeerConnection connection = await PeerConnection.AcceptAsync(
            raw,
            metadata,
            Handshake.NewPeerId(),
            ct);

        byte[] info = LieWith ?? InfoDictionaryOf(torrentFile);

        while (!ct.IsCancellationRequested)
        {
            if (await connection.ReceiveAsync(ct) is not Extended extended)
                continue;

            if (extended.ExtensionId == ExtensionHandshakeId)
            {
                await connection.SendAsync(new Extended(ExtensionHandshakeId, HandshakePayload(info.Length)), ct);
                continue;
            }

            if (extended.ExtensionId != OurExtensionId)
                continue;

            MetadataMessage request = MetadataMessage.Parse(extended.Payload);

            if (request.Type != MetadataMessageType.Request)
                continue;

            if (RejectEverything)
            {
                await connection.SendAsync(
                    new Extended((byte)ExtensionHandshake.OurUtMetadataId, MetadataMessage.WriteReject(request.Piece)),
                    ct);

                continue;
            }

            int offset = request.Piece * MetadataDownload.PieceLength;
            int length = Math.Min(MetadataDownload.PieceLength, info.Length - offset);

            await connection.SendAsync(
                new Extended(
                    (byte)ExtensionHandshake.OurUtMetadataId,
                    MetadataMessage.WriteData(request.Piece, info.Length, info.AsSpan(offset, length).ToArray())),
                ct);
        }
    }

    private byte[] HandshakePayload(int metadataSize) =>
        BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["m"] = SupportsNothing
                ? new BDictionary(new Dictionary<string, BValue>())
                : new BDictionary(new Dictionary<string, BValue> { ["ut_metadata"] = new BInteger(OurExtensionId) }),
            ["metadata_size"] = new BInteger(metadataSize),
        }));

    public static byte[] InfoDictionaryOf(byte[] torrentFile) =>
        BencodeWriter.Write(((BDictionary)BencodeReader.Parse(torrentFile)).Entries["info"]);
}
