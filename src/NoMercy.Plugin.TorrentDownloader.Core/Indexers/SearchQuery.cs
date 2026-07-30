// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public record SearchQuery(string ShowName, EpisodeSlot? Slot = null)
{
    public string Text => Slot is EpisodeSlot slot ? $"{ShowName} {slot}" : ShowName;
}
