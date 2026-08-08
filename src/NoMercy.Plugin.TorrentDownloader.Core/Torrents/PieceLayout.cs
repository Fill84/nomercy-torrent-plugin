// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

public readonly record struct FileSegment(FileEntry File, long OffsetInFile, int Length);

/// <summary>
/// Translates a range of the concatenated torrent stream into the files it actually
/// touches. Multi-file support is entirely this: a piece is a window, and a window
/// does not care where one file ends and the next begins.
/// </summary>
public static class PieceLayout
{
    public static IReadOnlyList<FileSegment> Segments(TorrentMetadata metadata, int pieceIndex)
    {
        if (pieceIndex < 0 || pieceIndex >= metadata.PieceCount)
            throw new ArgumentOutOfRangeException(nameof(pieceIndex), pieceIndex, "no such piece");

        return SegmentsFor(metadata, pieceIndex * metadata.PieceLength, metadata.LengthOfPiece(pieceIndex));
    }

    public static IReadOnlyList<FileSegment> SegmentsFor(TorrentMetadata metadata, long absoluteOffset, int length)
    {
        if (absoluteOffset < 0 || length < 0 || absoluteOffset + length > metadata.TotalLength)
            throw new ArgumentOutOfRangeException(nameof(absoluteOffset), absoluteOffset, "the range falls outside the torrent");

        List<FileSegment> segments = [];
        long remaining = length;
        long position = absoluteOffset;

        foreach (FileEntry file in metadata.Files)
        {
            if (remaining == 0)
                break;

            // A zero-length file occupies no bytes, so no range ever touches it.
            if (file.Length == 0 || position >= file.End || position < file.Offset)
                continue;

            long offsetInFile = position - file.Offset;
            int take = (int)Math.Min(remaining, file.Length - offsetInFile);

            segments.Add(new FileSegment(file, offsetInFile, take));

            position += take;
            remaining -= take;
        }

        return segments;
    }
}
