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

        // What is put to this is the release's *title*, which is what the
        // parser reads off the name before the season tag — never the whole
        // name. Both callers in the plugin pass that, and passing a whole name
        // here asked a question nothing asks.
        Assert.True(TitleMatcher.Matches("Silo", "Silo"));
        Assert.True(TitleMatcher.Matches("Silo 2023", "Silo"));
    }

    /// <remarks>
    /// <strong>Beginning with is not enough when the show is one ordinary
    /// word.</strong> On 22 August 2026 the owner's <em>Lucky</em> collected
    /// five other programmes this way — over a hundred rows in one cycle, every
    /// one of them judged as though it were the right show and refused for its
    /// resolution. Nothing was downloaded that day only because none of them
    /// was 1080p; a 1080p copy of any of them would have been taken and filed
    /// as the owner's episode.
    ///
    /// The year is the one addition that does not make it another programme.
    /// The library's own titles carry none — Silo, Lucky, Sugar, Lioness — and
    /// the sites post all four both ways.
    /// </remarks>
    [Theory]
    [InlineData("Lucky", true)]
    [InlineData("Lucky 2026", true)]
    [InlineData("Lucky Hank", false)]
    [InlineData("Lucky Dog", false)]
    [InlineData("Lucky 7", false)]
    [InlineData("Lucky Bastards", false)]
    [InlineData("Lucky 13 2024", false)]
    public void AShowNamedByOneOrdinaryWordTakesNoExtraWords(string releaseTitle, bool matches)
    {
        Assert.Equal(matches, TitleMatcher.Matches(releaseTitle, "Lucky"));
    }

    /// <remarks>
    /// <strong>A repost's tag and a file's extension are not the release's
    /// name.</strong> One release arrived on the owner's own library on
    /// 22 August 2026 as <c>Sugar 2024 S02E08 1080p WEB H264-CAKES</c> from the
    /// site that had it, as
    /// <c>sugar 2024 s02e08 1080p web h264-cakes[EZTVx to]</c> from one that
    /// reposted it, and as
    /// <c>Sugar.2024.S02E05.1080p.WEB.h264-ETHEL[EZTVx.to].mkv</c> from a
    /// third. Ten of twenty-two decisions kept a site's rendering because the
    /// tagged copy did not match the name a name database had published for it.
    ///
    /// A bracketed group anywhere but the end is left alone: anime writes its
    /// own group that way at the front, and there it is part of the name.
    /// </remarks>
    [Theory]
    [InlineData("Sugar 2024 S02E08 1080p WEB H264-CAKES", "sugar 2024 s02e08 1080p web h264-cakes[EZTVx to]")]
    [InlineData("Sugar.2024.S02E08.1080p.WEB.H264-CAKES", "Sugar 2024 S02E08 1080p WEB H264-CAKES[EZTVx.to].mkv")]
    [InlineData("Silo S03E08 1080p WEB H264-CAKES", "Silo.S03E08.1080p.WEB.H264-CAKES [TGx]")]
    public void ARepostsTagAndAFilesExtensionAreNotPartOfTheName(string published, string reposted)
    {
        Assert.Equal(TitleMatcher.Release(published), TitleMatcher.Release(reposted));
    }

    /// <remarks>
    /// Two different releases stay different, whatever is stuck on the end of
    /// them. Dropping a tag must not drop the group with it.
    /// </remarks>
    [Fact]
    public void TwoDifferentReleasesAreStillDifferent()
    {
        Assert.NotEqual(
            TitleMatcher.Release("Silo S03E08 1080p WEB H264-CAKES[EZTVx to]"),
            TitleMatcher.Release("Silo S03E08 1080p WEB H264-SYLiX[EZTVx to]"));

        // And an anime group at the front is the name, not a repost's tag.
        Assert.NotEqual(
            TitleMatcher.Release("[SubsPlease] Frieren - 12 (1080p)"),
            TitleMatcher.Release("[Erai-raws] Frieren - 12 (1080p)"));
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
