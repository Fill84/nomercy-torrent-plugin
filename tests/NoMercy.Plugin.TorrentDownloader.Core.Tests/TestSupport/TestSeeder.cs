// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// A seeder that exists only in tests.
///
/// <para>
/// The product never serves a piece to anybody. The test harness does, because that is
/// the only way to prove a real download from end to end without a network, an external
/// client, or a swarm that might not be there on a Tuesday.
/// </para>
///
/// <para>
/// It can also lie: send a corrupt piece, stall, or hang up mid-transfer. Error handling
/// that has never been provoked usually does not work.
/// </para>
/// </summary>
public sealed class TestSeeder(TorrentMetadata metadata, byte[] content)
{
    /// <summary>Piece indices this seeder will deliberately corrupt.</summary>
    public HashSet<int> CorruptPieces { get; } = [];

    /// <summary>Stop answering after this many blocks, without closing. Zero means never stall.</summary>
    public int StallAfterBlocks { get; set; }

    /// <summary>Hang up after this many blocks. Zero means never.</summary>
    public int HangUpAfterBlocks { get; set; }

    public int BlocksServed { get; private set; }

    public async Task ServeAsync(Stream raw, CancellationToken ct)
    {
        await using PeerConnection connection = await PeerConnection.AcceptAsync(
            raw,
            metadata,
            Handshake.NewPeerId(),
            ct);

        Bitfield complete = new(metadata.PieceCount);

        for (int index = 0; index < metadata.PieceCount; index++)
            complete[index] = true;

        await connection.SendAsync(new BitfieldMessage(complete.ToBytes()), ct);
        await connection.SendAsync(new Unchoke(), ct);

        while (!ct.IsCancellationRequested)
        {
            PeerMessage message = await connection.ReceiveAsync(ct);

            if (message is not Request request)
                continue;

            if (StallAfterBlocks > 0 && BlocksServed >= StallAfterBlocks)
                continue;

            if (HangUpAfterBlocks > 0 && BlocksServed >= HangUpAfterBlocks)
                return;

            await connection.SendAsync(new PieceBlock(request.PieceIndex, request.Begin, Block(request)), ct);
            BlocksServed++;
        }
    }

    private byte[] Block(Request request)
    {
        long start = request.PieceIndex * metadata.PieceLength + request.Begin;
        byte[] block = content.AsSpan((int)start, request.Length).ToArray();

        if (!CorruptPieces.Contains(request.PieceIndex))
            return block;

        // One flipped bit is enough. A corrupt piece that still hashes correctly would
        // not be corrupt, and a wholly different payload would be a weaker test.
        block[0] ^= 0xFF;

        return block;
    }
}
