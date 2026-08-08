// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public static class PieceVerifier
{
    public static bool Matches(TorrentMetadata metadata, int pieceIndex, ReadOnlySpan<byte> piece)
    {
        if (pieceIndex < 0 || pieceIndex >= metadata.PieceCount)
            return false;

        // A piece of the wrong length is wrong even if some prefix would hash correctly,
        // and hashing it would be a wasted SHA-1 pass.
        if (piece.Length != metadata.LengthOfPiece(pieceIndex))
            return false;

        Span<byte> actual = stackalloc byte[20];
        SHA1.HashData(piece, actual);

        return actual.SequenceEqual(metadata.PieceHashes[pieceIndex]);
    }
}
