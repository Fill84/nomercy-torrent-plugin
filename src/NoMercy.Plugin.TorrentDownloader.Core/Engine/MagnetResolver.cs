// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

/// <summary>
/// Turning a magnet link into a torrent.
///
/// <para>
/// A magnet names an info hash and nothing else. The piece lengths and the file list
/// live with the peers, so this dials a few of them, asks over BEP 9, and assembles
/// what comes back - refusing anything that does not hash to the info hash the magnet
/// named. Without that check a peer could hand us a different torrent entirely and we
/// would download it without noticing.
/// </para>
/// </summary>
public sealed class MagnetResolver(
    IReadOnlyList<IPeerSource> trackers,
    IPeerDialer dialer,
    byte[] localPeerId)
{
    /// <summary>Enough peers that one silent or unhelpful one does not stall the whole thing.</summary>
    private const int PeersToAsk = 4;

    private const int ExtensionHandshakeId = 0;

    public async Task<TorrentMetadata> ResolveAsync(
        MagnetLink magnet,
        IReadOnlyList<string> extraTrackers,
        TimeSpan timeout,
        CancellationToken ct)
    {
        List<string> announce = [.. magnet.Trackers];

        foreach (string tracker in extraTrackers)
        {
            if (!announce.Contains(tracker))
                announce.Add(tracker);
        }

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(timeout);

        IReadOnlyList<PeerEndPoint> peers = await FindPeersAsync(magnet, announce, attempt.Token);

        foreach (PeerEndPoint peer in peers.Take(PeersToAsk))
        {
            TorrentMetadata? metadata = await TryAskAsync(magnet, peer, announce, attempt.Token);

            if (metadata is not null)
                return metadata;
        }

        throw new MetadataException(
            $"no peer handed over the metadata for {Convert.ToHexStringLower(magnet.InfoHash)} within {timeout.TotalMinutes:0} minutes");
    }

    private async Task<IReadOnlyList<PeerEndPoint>> FindPeersAsync(
        MagnetLink magnet,
        IReadOnlyList<string> announce,
        CancellationToken ct)
    {
        List<PeerEndPoint> found = [];

        foreach (string url in announce)
        {
            IPeerSource? source = trackers.FirstOrDefault(candidate => candidate.CanAnnounceTo(url));

            if (source is null)
                continue;

            try
            {
                AnnounceResult result = await source.AnnounceAsync(
                    url,
                    new AnnounceRequest(magnet.InfoHash, localPeerId, 6881, 0, 0, 0, AnnounceEvent.Started),
                    ct);

                foreach (PeerEndPoint peer in result.Peers)
                {
                    if (!found.Contains(peer))
                        found.Add(peer);
                }
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // One tracker being down says nothing about the others.
            }
        }

        return found;
    }

    /// <summary>Null when this peer could not or would not help, which is not news.</summary>
    private async Task<TorrentMetadata?> TryAskAsync(
        MagnetLink magnet,
        PeerEndPoint peer,
        IReadOnlyList<string> announce,
        CancellationToken ct)
    {
        try
        {
            Stream raw = await dialer.ConnectAsync(peer, ct);

            await using PeerConnection connection = await PeerConnection.DialAsync(raw, magnet.InfoHash, localPeerId, ct);

            if (!connection.SupportsExtensionProtocol)
                return null;

            await connection.SendAsync(new Extended(ExtensionHandshakeId, ExtensionHandshake.Write(metadataSize: null)), ct);

            ExtensionHandshake theirs = await ReadHandshakeAsync(connection, ct);

            if (theirs.UtMetadataId is not int theirMetadataId || theirs.MetadataSize is not int size)
                return null;

            MetadataDownload download = new(magnet.InfoHash, size);

            foreach (int piece in download.MissingPieces().ToList())
                await connection.SendAsync(new Extended((byte)theirMetadataId, MetadataMessage.WriteRequest(piece)), ct);

            return await CollectAsync(connection, download, announce, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task<ExtensionHandshake> ReadHandshakeAsync(PeerConnection connection, CancellationToken ct)
    {
        while (true)
        {
            // A peer sends its bitfield and other chatter around the handshake. Reading
            // past it costs nothing; assuming the handshake is first costs the connection.
            if (await connection.ReceiveAsync(ct) is Extended { ExtensionId: ExtensionHandshakeId } extended)
                return ExtensionHandshake.Parse(extended.Payload);
        }
    }

    private static async Task<TorrentMetadata?> CollectAsync(
        PeerConnection connection,
        MetadataDownload download,
        IReadOnlyList<string> announce,
        CancellationToken ct)
    {
        while (!download.IsComplete)
        {
            if (await connection.ReceiveAsync(ct) is not Extended extended)
                continue;

            // Addressed with the id we asked them to use, so anything else is not ours.
            if (extended.ExtensionId != ExtensionHandshake.OurUtMetadataId)
                continue;

            MetadataMessage message = MetadataMessage.Parse(extended.Payload);

            if (message.Type == MetadataMessageType.Reject)
                return null;

            if (message.Type != MetadataMessageType.Data)
                continue;

            download.Accept(message.Piece, message.Data);
        }

        // Throws if the assembled bytes do not hash to the info hash the magnet named.
        return download.Build(announce);
    }
}
