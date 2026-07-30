// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record ReleaseProfile
{
    public required string Name { get; init; }
    public required QualityLadder Quality { get; init; }
    public LanguageProfile Language { get; init; } = LanguageProfile.EnglishOnly;
    public VideoCodec Codec { get; init; } = VideoCodec.Unknown;
    public bool RequireCodecTag { get; init; }
    public IReadOnlyList<string> BlockedGroups { get; init; } = [];
    public IReadOnlyList<GroupPreference> PreferredGroups { get; init; } = [];
    public IReadOnlyList<TermRule> Terms { get; init; } = [];
    public long? MinSizeBytes { get; init; }
    public long? MaxSizeBytes { get; init; }
    public int MinSeeders { get; init; }
    public bool AllowSeasonPacks { get; init; }
}
