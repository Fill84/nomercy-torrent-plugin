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

public record ReleaseInfo
{
    public required string IndexerName { get; init; }
    public required string TorrentId { get; init; }
    public required string Title { get; init; }
    public string? DetailUrl { get; init; }
    public string? MagnetUri { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoHash { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public int IndexerPriority { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
