// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public readonly record struct EpisodeSlot(int Season, int Episode)
{
    public override string ToString() => $"S{Season:00}E{Episode:00}";
}
