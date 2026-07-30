// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class ReleaseNameParserQualityTests
{
    [Theory]
    [InlineData("Silo S03E04 2160p WEB-DL", Resolution.Uhd2160)]
    [InlineData("Silo S03E04 4K WEB-DL", Resolution.Uhd2160)]
    [InlineData("Silo S03E04 1080p WEB-DL", Resolution.Fhd1080)]
    [InlineData("Silo S03E04 1080i HDTV", Resolution.Fhd1080)]
    [InlineData("Silo S03E04 720p HDTV", Resolution.Hd720)]
    [InlineData("Silo S03E04 576p PDTV", Resolution.Sd576)]
    [InlineData("Silo S03E04 480p WEBRip", Resolution.Sd480)]
    [InlineData("Silo S03E04 WEB-DL", Resolution.Unknown)]
    public void ParseQuality_ReadsResolution(string title, Resolution expected)
    {
        ReleaseNameParser.ParseQuality(title).Resolution.Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p BluRay REMUX", ReleaseSource.Remux)]
    [InlineData("Silo S03E04 1080p BluRay", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p Blu-Ray", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p BDRip", ReleaseSource.BluRay)]
    [InlineData("Silo S03E04 1080p WEB-DL", ReleaseSource.WebDl)]
    [InlineData("Silo S03E04 1080p WEBDL", ReleaseSource.WebDl)]
    [InlineData("Silo S03E04 1080p WEBRip", ReleaseSource.WebRip)]
    [InlineData("Silo S03E04 1080p WEB", ReleaseSource.WebRip)]
    [InlineData("Silo S03E04 1080p HDTV", ReleaseSource.Hdtv)]
    [InlineData("Silo S03E04 DVDRip", ReleaseSource.DvdRip)]
    [InlineData("Silo S03E04 1080p", ReleaseSource.Unknown)]
    public void ParseQuality_ReadsSourceMostSpecificFirst(string title, ReleaseSource expected)
    {
        ReleaseNameParser.ParseQuality(title).Source.Should().Be(expected);
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB x265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB H265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB H 265-CAKES", VideoCodec.H265)]
    [InlineData("Silo.S03E04.1080p.WEB.H.265-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB HEVC-CAKES", VideoCodec.H265)]
    [InlineData("Silo S03E04 1080p WEB x264-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB H 264-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB AVC-CAKES", VideoCodec.H264)]
    [InlineData("Silo S03E04 1080p WEB AV1-CAKES", VideoCodec.Av1)]
    [InlineData("Silo S03E04 1080p HDTV-CAKES", VideoCodec.Unknown)]
    public void ParseCodec_ReadsEverySpelling(string title, VideoCodec expected)
    {
        ReleaseNameParser.ParseCodec(title).Should().Be(expected);
    }
}
