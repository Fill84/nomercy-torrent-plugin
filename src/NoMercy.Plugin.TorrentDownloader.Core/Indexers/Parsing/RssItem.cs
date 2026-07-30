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

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public record RssItem
{
    public required string Title { get; init; }
    public string? Link { get; init; }
    public string? Guid { get; init; }
    public DateTimeOffset? Published { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string? EnclosureUrl { get; init; }
    public long EnclosureLength { get; init; }
    public string? EnclosureType { get; init; }
}
