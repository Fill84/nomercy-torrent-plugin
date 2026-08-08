// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

/// <summary>
/// One file in a torrent. <paramref name="Offset"/> is where it starts within the
/// concatenated stream that the pieces hash over, which is what lets a piece
/// spanning two files be written to both.
/// </summary>
public sealed record FileEntry(IReadOnlyList<string> Path, long Length, long Offset)
{
    public long End => Offset + Length;
}
