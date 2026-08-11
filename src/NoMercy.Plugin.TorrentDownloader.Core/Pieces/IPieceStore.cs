// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public interface IPieceStore
{
    Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> piece, CancellationToken ct);

    /// <summary>
    /// Returns the piece as it currently sits on disk. Bytes that were never written
    /// read as zero, which the verifier rejects - that is how resume detects a file
    /// the user deleted rather than trusting a record that says it is present.
    /// </summary>
    Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken ct);

    /// <summary>
    /// Whether this piece can be handed to a peer in full.
    ///
    /// <para>
    /// A store that keeps only some of a torrent's files cannot serve a piece that overlaps
    /// the rest: those bytes were never written and read back as zeroes. Serving them would
    /// make us the peer that lies.
    /// </para>
    /// </summary>
    bool CanServe(int pieceIndex) => true;

    /// <summary>
    /// Returns only once the bytes are durable. The resume record is written after this
    /// returns, because the invariant is that the record never claims more than the disk holds.
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}
