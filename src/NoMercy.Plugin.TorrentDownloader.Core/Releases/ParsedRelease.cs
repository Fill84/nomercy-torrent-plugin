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

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record ParsedRelease
{
    public required string Title { get; init; }
    public EpisodeSlot? Episode { get; init; }
    public int? SeasonPack { get; init; }
    public Quality Quality { get; init; }
    public VideoCodec Codec { get; init; }
    public string? ReleaseGroup { get; init; }
    public bool IsProper { get; init; }
    public bool IsRepack { get; init; }
    public IReadOnlyList<string> Languages { get; init; } = [];
    public bool IsDualAudio { get; init; }
}
