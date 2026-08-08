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
    /// Returns only once the bytes are durable. The resume record is written after this
    /// returns, because the invariant is that the record never claims more than the disk holds.
    /// </summary>
    Task FlushAsync(CancellationToken ct);
}
