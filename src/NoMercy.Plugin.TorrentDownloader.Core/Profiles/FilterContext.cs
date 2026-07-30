// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record FilterContext(
    string ShowName,
    EpisodeSlot? WantedSlot,
    ReleaseProfile Profile,
    IReadOnlySet<string> BlacklistedNormalisedTitles,
    IReadOnlySet<string> BlacklistedInfoHashes
);
