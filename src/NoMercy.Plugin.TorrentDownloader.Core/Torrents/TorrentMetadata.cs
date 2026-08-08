// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public sealed record TorrentMetadata(
    byte[] InfoHash,
    string Name,
    long PieceLength,
    IReadOnlyList<byte[]> PieceHashes,
    IReadOnlyList<FileEntry> Files,
    IReadOnlyList<string> Trackers)
{
    public long TotalLength => Files.Sum(file => file.Length);

    public int PieceCount => PieceHashes.Count;

    /// <summary>The last piece is short unless the total divides evenly.</summary>
    public int LengthOfPiece(int index)
    {
        long start = index * PieceLength;
        return (int)Math.Min(PieceLength, TotalLength - start);
    }
}

public sealed class MetadataException(string message) : Exception(message);
