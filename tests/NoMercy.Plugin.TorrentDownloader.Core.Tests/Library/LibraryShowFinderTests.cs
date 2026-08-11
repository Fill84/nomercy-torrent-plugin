// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Library;

/// <summary>
/// Finding one show by what somebody typed.
///
/// <para>
/// This is the only door to a show the plugin does not already hold, so it has to be
/// forgiving enough to be usable by hand and strict enough never to follow a show nobody
/// meant. Every rule below is one or the other.
/// </para>
/// </summary>
public class LibraryShowFinderTests
{
    private static LibraryShow Show(int id, string title, int? year = 2024) =>
        new(id, title, year, "lib-1", $"/media/{id}", 10, 0);

    private static readonly LibraryShow[] Library =
    [
        Show(1, "Silo", 2023),
        Show(2, "Lucky", 2026),
        Show(3, "Lucky Luke", 1984),
        Show(4, "Lucky Luke", 2026),
        Show(5, "The Simpsons", 1989),
    ];

    [Fact]
    public void Find_TakesAShowByItsExactName()
    {
        ShowMatch match = LibraryShowFinder.Find(Library, "Silo");

        match.Outcome.Should().Be(ShowLookup.One);
        match.Show!.ShowId.Should().Be(1);
    }

    [Theory]
    [InlineData("silo")]
    [InlineData("SILO")]
    [InlineData("  Silo  ")]
    public void Find_DoesNotCareHowItWasTyped(string typed)
    {
        LibraryShowFinder.Find(Library, typed).Show!.ShowId.Should().Be(1);
    }

    [Fact]
    public void Find_TakesPartOfAName()
    {
        LibraryShowFinder.Find(Library, "simpson").Show!.ShowId.Should().Be(5);
    }

    /// <summary>
    /// "Lucky" is both a show of its own and the start of two others. Without an exact pass
    /// first, the shorter title could never be typed at all.
    /// </summary>
    [Fact]
    public void Find_PrefersAnExactNameOverEverythingItIsThePrefixOf()
    {
        ShowMatch match = LibraryShowFinder.Find(Library, "Lucky");

        match.Outcome.Should().Be(ShowLookup.One);
        match.Show!.ShowId.Should().Be(2);
    }

    /// <summary>
    /// Two shows with the same title is the ambiguity that actually happens - a remake, or
    /// a reboot. The plugin will not pick, and says which two so the next attempt can.
    /// </summary>
    [Fact]
    public void Find_RefusesToChooseBetweenTwoShowsOfTheSameName()
    {
        ShowMatch match = LibraryShowFinder.Find(Library, "Lucky Luke");

        match.Outcome.Should().Be(ShowLookup.Several);
        match.Show.Should().BeNull();
        match.Candidates.Should().Equal("Lucky Luke (1984)", "Lucky Luke (2026)");
    }

    [Theory]
    [InlineData("Lucky Luke (1984)", 3)]
    [InlineData("Lucky Luke 1984", 3)]
    [InlineData("Lucky Luke [2026]", 4)]
    public void Find_TellsThemApartByTheYear(string typed, int expected)
    {
        ShowMatch match = LibraryShowFinder.Find(Library, typed);

        match.Outcome.Should().Be(ShowLookup.One);
        match.Show!.ShowId.Should().Be(expected);
    }

    [Fact]
    public void Find_SaysNothingMatchedRatherThanGuessing()
    {
        LibraryShowFinder.Find(Library, "Breaking Bad").Outcome.Should().Be(ShowLookup.None);
    }

    [Fact]
    public void Find_FindsNothingForTheRightNameAndTheWrongYear()
    {
        LibraryShowFinder.Find(Library, "Silo (1999)").Outcome.Should().Be(ShowLookup.None);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Find_TakesAnEmptyBoxAsNothingRatherThanEverything(string? typed)
    {
        LibraryShowFinder.Find(Library, typed).Outcome.Should().Be(ShowLookup.None);
    }

    /// <summary>
    /// A bare year is a title as far as this is concerned - there are shows called "1883"
    /// and "2012" - and stripping it would leave an empty search that matches the whole
    /// library.
    /// </summary>
    [Fact]
    public void Find_TreatsAYearOnItsOwnAsATitle()
    {
        LibraryShow[] library = [Show(9, "1883", 2021), Show(1, "Silo", 2023)];

        ShowMatch match = LibraryShowFinder.Find(library, "1883");

        match.Outcome.Should().Be(ShowLookup.One);
        match.Show!.ShowId.Should().Be(9);
    }

    [Fact]
    public void Find_NamesAShowWithNoYearWithoutInventingOne()
    {
        LibraryShow[] library = [Show(1, "Repeat", null), Show(2, "Repeat", 2020)];

        LibraryShowFinder.Find(library, "Repeat").Candidates.Should().Equal("Repeat", "Repeat (2020)");
    }

    [Fact]
    public void Find_FindsNothingInAnEmptyLibrary()
    {
        LibraryShowFinder.Find([], "Silo").Outcome.Should().Be(ShowLookup.None);
    }
}
