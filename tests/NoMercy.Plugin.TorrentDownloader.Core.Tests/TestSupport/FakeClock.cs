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

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

public sealed class FakeClock(DateTimeOffset start) : IClock
{
    private DateTimeOffset _now = start;

    public DateTimeOffset UtcNow => _now;

    public List<TimeSpan> Delays { get; } = [];

    public void Advance(TimeSpan duration) => _now = _now.Add(duration);

    public Task DelayAsync(TimeSpan duration, CancellationToken ct)
    {
        Delays.Add(duration);
        _now = _now.Add(duration);
        return Task.CompletedTask;
    }
}
