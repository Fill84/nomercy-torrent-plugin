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

public class ReleaseNameParserGroupTests
{
    [Theory]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", "CAKES")]
    [InlineData("Silo S03E04 1080p WEB H264-NTb", "NTb")]
    [InlineData("Some.Show.S01E01.1080p.WEB-DL-Group_Name", "Group_Name")]
    [InlineData("[SubsPlease] Frieren - 01 (1080p) [ABCD1234]", "SubsPlease")]
    [InlineData("[Erai-raws] Show - 12 [1080p]", "Erai-raws")]
    [InlineData("Silo S03E04 1080p WEB h264-ETHEL[eztv.re]", "ETHEL")]
    public void ParseGroup_ReadsSceneAndFansubConventions(string title, string expected)
    {
        ReleaseNameParser.ParseGroup(title).Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB H264")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseGroup_ReturnsNullWhenNoGroupIsNamed(string? title)
    {
        ReleaseNameParser.ParseGroup(title).Should().BeNull();
    }

    [Theory]
    [InlineData("Silo.S03E04.PROPER.1080p.WEB.H264-CAKES", true, false)]
    [InlineData("Silo.S03E04.REPACK.1080p.WEB.H264-CAKES", false, true)]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", false, false)]
    public void Parse_ReadsProperAndRepackFlags(string title, bool proper, bool repack)
    {
        ParsedRelease parsed = ReleaseNameParser.Parse(title);
        parsed.IsProper.Should().Be(proper);
        parsed.IsRepack.Should().Be(repack);
    }

    [Fact]
    public void Parse_FillsEveryFieldFromOneTitle()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Silo.S03E04.1080p.WEB-DL.H264-CAKES");

        parsed.Title.Should().Be("Silo.S03E04.1080p.WEB-DL.H264-CAKES");
        parsed.Episode.Should().Be(new EpisodeSlot(3, 4));
        parsed.SeasonPack.Should().BeNull();
        parsed.Quality.Should().Be(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl));
        parsed.Codec.Should().Be(VideoCodec.H264);
        parsed.ReleaseGroup.Should().Be("CAKES");
        parsed.IsProper.Should().BeFalse();
        parsed.IsRepack.Should().BeFalse();
    }

    [Fact]
    public void Parse_FillsSeasonPackWhenNoEpisodeIsNamed()
    {
        ParsedRelease parsed = ReleaseNameParser.Parse("Silo.S03.1080p.WEB-DL.H264-CAKES");

        parsed.Episode.Should().BeNull();
        parsed.SeasonPack.Should().Be(3);
    }
}
