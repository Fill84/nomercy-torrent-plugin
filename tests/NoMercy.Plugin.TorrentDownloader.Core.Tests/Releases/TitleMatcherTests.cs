using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Releases;

public class TitleMatcherTests
{
    [Theory]
    [InlineData("Lucky 2026 S01E02 1080p WEB H264-CAKES", "Lucky")]
    [InlineData("Lucky.2026.S01E02.1080p", "Lucky")]
    [InlineData("Big Brother US S28E08 1080p", "Big Brother US")]
    [InlineData("Big Brother US S28E08 1080p", "Big Brother")]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "Silo")]
    [InlineData("Silo S03 1080p WEB H264-CAKES", "Silo")]
    public void Matches_AcceptsTheNameLeadingTheTitle(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Special Ops Lioness S02E01 1080p", "Lioness")]
    [InlineData("[ToonsHub] The World Is Dancing S01E04 1080p", "The World Is Dancing")]
    public void Matches_AcceptsTheNameEndingWhereTheMarkerBegins(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Theory]
    [InlineData("Lucky Hank S01E02 1080p", "Lucky")]
    [InlineData("We.Were.the.Lucky.Ones.S01E01.1080p", "Lucky")]
    [InlineData("Unlucky S01E01 1080p", "Lucky")]
    [InlineData("Silo S03E04 The Lucky One 1080p", "Lucky")]
    public void Matches_RejectsTheNameAppearingAnywhereElse(string title, string showName)
    {
        TitleMatcher.Matches(title, showName).Should().BeFalse();
    }

    [Fact]
    public void Matches_AcceptsAGluedLeadingTokenFromSearchHighlighting()
    {
        TitleMatcher.Matches("Lucky2026 S01E02 1080p", "Lucky").Should().BeTrue();
    }

    [Fact]
    public void Matches_DoesNotApplyTheGluedFallbackToTheTrailingPosition()
    {
        TitleMatcher.Matches("OpsLioness S02E01 1080p", "Lioness").Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Matches_RejectsAnEmptyShowName(string? showName)
    {
        TitleMatcher.Matches("Silo S03E04 1080p", showName).Should().BeFalse();
    }

    [Theory]
    [InlineData("Elite S01E01 1080p WEB", "Élite")]
    [InlineData("Pokemon S01E01 1080p WEB", "Pokémon")]
    public void Matches_AcceptsAReleaseThatStrippedDiacriticsFromTheShowName(
        string title,
        string showName
    )
    {
        TitleMatcher.Matches(title, showName).Should().BeTrue();
    }

    [Fact]
    public void Normalize_FoldsDiacritics()
    {
        TitleMatcher.Normalize("Pokémon").Should().Be("pokemon");
    }

    [Theory]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "silos03e041080pwebh264cakes")]
    [InlineData("Lucky Hank", "luckyhank")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_StripsEverythingButLowercaseAlphanumerics(string? text, string expected)
    {
        TitleMatcher.Normalize(text).Should().Be(expected);
    }
}
