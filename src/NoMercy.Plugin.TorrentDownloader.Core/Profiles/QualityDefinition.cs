// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityDefinition(string Name, Resolution Resolution, ReleaseSource Source)
{
    public bool Matches(Quality quality) =>
        Resolution == quality.Resolution
        && (Source == ReleaseSource.Unknown || Source == quality.Source);

    public bool IsSourceSpecific => Source != ReleaseSource.Unknown;
}
