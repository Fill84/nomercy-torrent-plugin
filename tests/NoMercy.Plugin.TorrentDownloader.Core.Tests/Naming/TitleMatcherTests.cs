using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Naming;

/// <summary>
/// Whether a release is for the show that was asked about.
/// </summary>
public class TitleMatcherTests
{
    /// <remarks>
    /// Begins with, never contains. <em>A Bloody Lucky Day</em> contains
    /// <em>Lucky</em> and is a different programme, and the library really does
    /// hold a show called <em>Lucky</em>.
    /// </remarks>
    [Fact]
    public void AReleaseMustBeginWithTheShowTitleRatherThanMerelyContainIt()
    {
        Assert.True(TitleMatcher.Matches("Lucky", "Lucky"));
        Assert.False(TitleMatcher.Matches("A Bloody Lucky Day", "Lucky"));
    }

    /// <remarks>
    /// And beginning with is counted in words. <em>Silos</em> begins with the
    /// letters of <em>Silo</em> and is not that show — a row on the real
    /// LimeTorrents capture is titled <c>Silos / Silo (2023–)</c>, which is how
    /// this was noticed rather than imagined.
    /// </remarks>
    [Fact]
    public void AWordThatMerelyStartsWithTheTitleIsADifferentWord()
    {
        Assert.False(TitleMatcher.Matches("Silos / Silo (2023–) S03E06", "Silo"));
        Assert.True(TitleMatcher.Matches("Silo S03E06 1080p WEB H264-CAKES", "Silo"));
    }

    /// <remarks>
    /// Punctuation and case are how a site writes a title, not what it is. The
    /// scene name here is real, off the Nyaa capture, and the show title is the
    /// one the library holds.
    /// </remarks>
    [Fact]
    public void PunctuationAndCaseAreNotADifference()
    {
        Assert.True(TitleMatcher.Matches(
            "Frieren Beyond Journey s End",
            "Frieren: Beyond Journey's End"));

        Assert.True(TitleMatcher.Matches("SILO", "Silo"));
    }

    /// <remarks>
    /// Nor is an accent. One row of the Nyaa capture writes the same programme
    /// both ways in the one title — <c>Pokémon Horizons: The Series</c> and
    /// <c>Pokemon (2023)</c> — so a match that insists on the accent refuses a
    /// release of exactly the show that was asked for.
    /// </remarks>
    [Fact]
    public void AnAccentIsNotADifferenceEither()
    {
        Assert.True(TitleMatcher.Matches("Pokémon Horizons: The Series", "Pokemon Horizons"));
        Assert.True(TitleMatcher.Matches("Pokemon Horizons: The Series", "Pokémon Horizons"));
    }

    /// <remarks>
    /// A letter is anything a language calls one. The Nyaa capture carries a
    /// title written in Japanese, and a matcher that only knows the Latin
    /// alphabet throws every character of it away and is left comparing two
    /// empty strings — which match each other and nothing anybody asked for.
    /// </remarks>
    [Fact]
    public void ATitleInAnotherAlphabetIsStillATitle()
    {
        Assert.True(TitleMatcher.Matches("雨の中での狂気 InsaneInTheRain", "雨の中での狂気"));
        Assert.False(TitleMatcher.Matches("雨の中での狂気 InsaneInTheRain", "狂気"));
    }

    /// <remarks>
    /// A title the release does not carry at all is not a match, however much
    /// of the rest of the name looks right.
    /// </remarks>
    [Fact]
    public void ADifferentShowDoesNotMatch()
    {
        Assert.False(TitleMatcher.Matches("Greek S01E01 HR HDTV XviD-2HD", "Silo"));
    }

    /// <remarks>
    /// Nothing matches nothing. A show with no title is a bug upstream, and
    /// answering true would put every release in the pool under it.
    /// </remarks>
    [Fact]
    public void AnEmptyTitleMatchesNothing()
    {
        Assert.False(TitleMatcher.Matches("Silo S03E06", string.Empty));
        Assert.False(TitleMatcher.Matches(string.Empty, "Silo"));
    }
}
