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

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class SizeParserTests
{
    [Theory]
    [InlineData("1.4 GB", 1503238553L)]
    [InlineData("700 MB", 734003200L)]
    [InlineData("2.5GB", 2684354560L)]
    [InlineData("1,234 MB", 1293942784L)]
    [InlineData("4 TB", 4398046511104L)]
    [InlineData("512 KB", 524288L)]
    public void Parse_ReadsUnitAndValue(string text, long expected)
    {
        SizeParser.Parse(text).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("N/A")]
    [InlineData("unknown")]
    public void Parse_ReturnsZeroWhenNothingParses(string? text)
    {
        SizeParser.Parse(text).Should().Be(0L);
    }
}
