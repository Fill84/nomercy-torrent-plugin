// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class ReleaseNameParserEpisodeTests
{
    [Theory]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES", 3, 4)]
    [InlineData("Silo s03e04 1080p", 3, 4)]
    [InlineData("Silo 3x04 1080p", 3, 4)]
    [InlineData("Silo Season 3 Episode 4", 3, 4)]
    [InlineData("Some Show S01E123 1080p", 1, 123)]
    public void ParseEpisode_ReadsEverySupportedNotation(string title, int season, int episode)
    {
        ReleaseNameParser.ParseEpisode(title).Should().Be(new EpisodeSlot(season, episode));
    }

    [Theory]
    [InlineData("Silo S03 1080p WEB H264-CAKES")]
    [InlineData("Silo 2026 1080p WEB")]
    [InlineData("")]
    [InlineData(null)]
    public void ParseEpisode_ReturnsNullWhenNoEpisodeIsNamed(string? title)
    {
        ReleaseNameParser.ParseEpisode(title).Should().BeNull();
    }

    [Fact]
    public void ParseEpisode_TakesTheEarliestMarkerNotTheLast()
    {
        ReleaseNameParser
            .ParseEpisode("Silo S03E04 The 1x02 Incident 1080p")
            .Should()
            .Be(new EpisodeSlot(3, 4));
    }

    [Fact]
    public void ParseEpisode_IgnoresACrossNotationGluedToMoreDigits()
    {
        ReleaseNameParser.ParseEpisode("Show 1920x1080 1080p").Should().BeNull();
    }

    [Theory]
    [InlineData("Silo.S03.1080p.WEB.H264-CAKES", 3)]
    [InlineData("Silo S02 1080p", 2)]
    [InlineData("Silo Season 3 COMPLETE 1080p", 3)]
    public void ParseSeasonPack_ReadsTheSeasonWhenNoEpisodeIsNamed(string title, int season)
    {
        ReleaseNameParser.ParseSeasonPack(title).Should().Be(season);
    }

    [Fact]
    public void ParseSeasonPack_ReturnsNullWhenTheTitleNamesAnEpisode()
    {
        ReleaseNameParser.ParseSeasonPack("Silo.S03E04.1080p").Should().BeNull();
    }

    [Fact]
    public void ParseSeasonPack_DoesNotTreatASpaceSeparatedEpisodeMarkerAsAPack()
    {
        ReleaseNameParser.ParseSeasonPack("Show S03 E04 1080p").Should().BeNull();
    }

    [Theory]
    [InlineData("Show S03 E04 1080p", 3, 4)]
    [InlineData("Show S03.E04 1080p", 3, 4)]
    [InlineData("Show S03_E04 1080p", 3, 4)]
    [InlineData("Show S03-E04 1080p", 3, 4)]
    public void ParseEpisode_AllowsASeparatorBetweenSeasonAndEpisode(
        string title,
        int season,
        int episode
    )
    {
        ReleaseNameParser.ParseEpisode(title).Should().Be(new EpisodeSlot(season, episode));
    }

    [Fact]
    public void EpisodeMarkerIndex_PointsAtTheStartOfTheMarker()
    {
        ReleaseNameParser.EpisodeMarkerIndex("Silo S03E04 1080p").Should().Be(5);
    }

    [Fact]
    public void EpisodeMarkerIndex_ReturnsNullWhenThereIsNoMarker()
    {
        ReleaseNameParser.EpisodeMarkerIndex("Silo 2026 1080p").Should().BeNull();
    }
}
