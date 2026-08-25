using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Naming;

/// <summary>
/// What an episode is called once this plugin has it.
/// </summary>
/// <remarks>
/// <para>
/// The name says which episode at which quality and nothing else, so two
/// releases of one episode at one quality come to the same name — and the same
/// path. A second copy of an episode cannot exist because there is nowhere for
/// it to be.
/// </para>
/// <para>
/// The owner's intake folder held ten files for five episodes, in pairs
/// differing only by the site's tag on the end, because the name came from the
/// release rather than from the episode.
/// </para>
/// </remarks>
public class EpisodeNameTests
{
    [Fact]
    public void AnEpisodeIsItsShowItsYearItsNumberAndItsQuality()
    {
        Assert.Equal(
            "Sugar.2024.S02E02.1080p.mkv",
            EpisodeName.For("Sugar", 2024, new(1, 2, 2), "1080p", ".mkv"));
    }

    /// <remarks>
    /// Spaces become dots, as every release this plugin reads is written.
    /// </remarks>
    [Fact]
    public void ASpaceInTheShowsNameBecomesADot()
    {
        Assert.Equal(
            "Rick.and.Morty.2013.S06E03.1080p.mkv",
            EpisodeName.For("Rick and Morty", 2013, new(1, 6, 3), "1080p", ".mkv"));
    }

    /// <remarks>
    /// <para>
    /// What is not known is left out rather than guessed at. A name carrying
    /// <c>unknown</c> or a year of 0 would be written into the owner's folder
    /// and parsed back by the server as part of the show's title.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(null, "1080p", "Lucky.S01E07.1080p.mkv")]
    [InlineData(2026, null, "Lucky.2026.S01E07.mkv")]
    [InlineData(null, null, "Lucky.S01E07.mkv")]
    public void WhatIsNotKnownIsLeftOut(int? year, string? resolution, string expected)
    {
        Assert.Equal(expected, EpisodeName.For("Lucky", year, new(1, 1, 7), resolution, ".mkv"));
    }

    /// <remarks>
    /// A file system will not take a colon, and a release title is text off a
    /// web page. One of them would fail the copy rather than the naming.
    /// </remarks>
    [Fact]
    public void ACharacterAFileSystemWillNotTakeIsDropped()
    {
        Assert.Equal(
            "Marvels.X-Men.97.2024.S01E05.2160p.mkv",
            EpisodeName.For("Marvel's X-Men: '97", 2024, new(1, 1, 5), "2160p", ".mkv"));
    }

    /// <remarks>
    /// <para>
    /// <strong>The same episode is named the same on every server.</strong>
    /// The rule used to be <c>Path.GetInvalidFileNameChars()</c>, which answers
    /// with seven characters on Windows and effectively none on Linux — so a
    /// Linux server wrote names carrying a colon or a question mark, which no
    /// Windows client can open, for the same episode a Windows server named
    /// cleanly.
    /// </para>
    /// <para>
    /// Every character here is one Windows refuses. They are checked one at a
    /// time so that a failure names the character rather than a whole string,
    /// and this test is the reason the set is written down rather than asked
    /// for.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData('<')]
    [InlineData('>')]
    [InlineData(':')]
    [InlineData('"')]
    [InlineData('/')]
    [InlineData('\\')]
    [InlineData('|')]
    [InlineData('?')]
    [InlineData('*')]
    public void NoCharacterWindowsRefusesSurvivesOnAnyPlatform(char refused)
    {
        string named = EpisodeName.For($"Silo{refused}", 2023, new(1, 3, 6), "1080p", ".mkv");

        Assert.DoesNotContain(refused, named);
        Assert.StartsWith("Silo.", named, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Two digits is the shape every release uses, and a longer number keeps
    /// its own length rather than being cut to fit.
    /// </remarks>
    [Theory]
    [InlineData(1, 7, "S01E07")]
    [InlineData(12, 134, "S12E134")]
    public void SeasonAndEpisodeAreTwoDigitsOrAsManyAsItTakes(int season, int episode, string expected)
    {
        Assert.Contains(expected, EpisodeName.For("Silo", null, new(1, season, episode), null, ".mkv"));
    }
}
