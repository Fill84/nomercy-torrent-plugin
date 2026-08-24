using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
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
    /// <strong>A name is accepted in exactly two places.</strong> Leading the
    /// title, where only a year or a country may follow it, or ending it, which
    /// is where a franchise prefix leaves it: a release of <em>Lioness</em> is
    /// posted as <em>Special Ops Lioness</em>, and refusing that loses the show
    /// entirely.
    ///
    /// Both positions and the country list come from the owner's own working
    /// tool, whose comment on that list says what this is guarding: every entry
    /// is a token a name can swallow, so a loose one reopens the fault where
    /// <em>Lucky</em> matched five other programmes.
    /// </remarks>
    [Theory]
    [InlineData("Lioness", "Lioness", true)]
    [InlineData("Lioness", "Lioness 2023", true)]
    [InlineData("Lioness", "Special Ops Lioness", true)]
    [InlineData("Lioness", "Lioness Hank", false)]
    [InlineData("Big Brother", "Big Brother US", true)]
    [InlineData("Lucky", "Lucky Hank", false)]
    [InlineData("Lucky", "We Were the Lucky Ones", false)]
    public void ANameLeadsTheTitleOrEndsItAndNothingElse(string show, string releaseTitle, bool matches)
    {
        Assert.Equal(matches, TitleMatcher.Matches(releaseTitle, show));
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

    // Written bare, with no brackets at all, which is how TorrentBay and
    // TorrentGalaxy repost EZTV's rows. It kept the copy of exactly the release
    // SceneSource had published from matching it, so an x265 re-encode won
    // Silo S03E04 on a seeder count.
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "Silo S03E04 1080p WEB H264-CAKES EZTV")]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES", "Silo S03E04 1080p WEB H264-CAKES EZTVx to")]
    public void ARepostsTagAndAFilesExtensionAreNotPartOfTheName(string published, string reposted)
    {
        Assert.Equal(TitleMatcher.Release(published), TitleMatcher.Release(reposted));
    }

    /// <remarks>
    /// <para>
    /// <strong>What the owner reads is the release, not the site.</strong>
    /// <c>Release</c> answers a comparison key — lower case, punctuation gone —
    /// which is right for deciding that two rows are one torrent and wrong for
    /// anything a person looks at. On 22 August 2026 the Downloads page said
    /// <c>Sugar 2024 S02E04 1080p WEB H264-CAKES EZTV</c>, and that name was
    /// written against the grab and carried into staging.
    /// </para>
    /// <para>
    /// The name as the group published it, with the reposter's tag and the file
    /// type taken off and nothing else touched: the case, the dots and the
    /// dashes are the release's own.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Sugar 2024 S02E04 1080p WEB H264-CAKES EZTV", "Sugar 2024 S02E04 1080p WEB H264-CAKES")]
    [InlineData("Silo.S03E08.1080p.WEB.H264-CAKES [TGx]", "Silo.S03E08.1080p.WEB.H264-CAKES")]
    [InlineData("Sugar 2024 S02E08 1080p WEB H264-CAKES[EZTVx.to].mkv", "Sugar 2024 S02E08 1080p WEB H264-CAKES")]
    [InlineData("Silo S03E04 1080p WEB H264-CAKES EZTVx to", "Silo S03E04 1080p WEB H264-CAKES")]

    // Untouched: there is nothing on the end of it but the group.
    [InlineData("Lucky 2026 S01E02 1080p WEB h264-ETHEL", "Lucky 2026 S01E02 1080p WEB h264-ETHEL")]
    [InlineData("Greek S01E01 HR HDTV XviD-2HD", "Greek S01E01 HR HDTV XviD-2HD")]

    // Anime, where brackets are part of the name at both ends: the group at the
    // front and the checksum at the back. Taking any bracketed group off the
    // end costs the checksum and then costs (1080p) as well, and a release that
    // does not say its resolution is refused for it.
    [InlineData(
        "[SubsPlease] Rilakkuma - 20 (1080p) [A830B1C2]",
        "[SubsPlease] Rilakkuma - 20 (1080p) [A830B1C2]")]
    public void TheNameTheOwnerReadsIsTheReleaseAndNotTheSite(string printed, string published)
    {
        Assert.Equal(published, TitleMatcher.Clean(printed));
    }

    /// <remarks>
    /// <para>
    /// <strong>One list of video types, and it is the whitelist.</strong> There
    /// were three of them in this plugin — the whitelist that decides what is
    /// downloaded, and two regular expressions here — and they agreed only
    /// because somebody kept them agreeing. One had already drifted: it carried
    /// <c>iso</c>, <c>rar</c> and <c>zip</c> as though they were video.
    /// </para>
    /// <para>
    /// A type added to the whitelist is recognised here without anything else
    /// being touched, and this fails if the two are ever written out
    /// separately again.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryVideoTypeTheWhitelistAllowsIsReadAsOne()
    {
        foreach (string extension in Staging.VideoExtensions)
        {
            string type = extension.TrimStart('.');
            string named = $"Silo.S03E06.1080p.WEB.H264-CAKES{extension}";

            // Read as a file type at all...
            Assert.Equal(type, TitleMatcher.FileType(named), StringComparer.OrdinalIgnoreCase);

            // ...and taken off the name, whole. A shorter type matching first
            // would leave the tail of a longer one behind: .m2ts answering "ts".
            Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", TitleMatcher.Clean(named));
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>The six files really in the owner's intake folder, against the
    /// grabs that really put them there</strong>, read off both on
    /// 24 August 2026. They were staged before the file was named after its
    /// release, so each carries the uploader's spelling and its grab carries
    /// the plugin's — and matching them is the whole of how those episodes get
    /// their encode asked for at last.
    /// </para>
    /// <para>
    /// Written out rather than reasoned about. "It should normalise to the
    /// same thing" is exactly the kind of claim that has been wrong here
    /// before.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(
        "Sugar.2024.S02E07.1080p.WEB.h264-ETHEL[EZTVx.to]",
        "Sugar 2024 S02E07 1080p WEB h264-ETHEL EZTV")]
    [InlineData(
        "silo.s03e04.1080p.web.h264-cakes[EZTVx.to]",
        "Silo S03E04 1080p WEB H264-CAKES")]
    [InlineData(
        "lioness.2023.s03e04.1080p.web.h264-cakes[EZTVx.to]",
        "Lioness 2023 S03E04 1080p WEB H264-CAKES")]
    [InlineData(
        "Sugar 2024 S02E03 Watch Face 1080p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB[EZTVx.to]",
        "Sugar 2024 S02E03 Watch Face 1080p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB EZTV")]
    [InlineData(
        "Rick and Morty S09E10 Field of Dreams REPACK 1080p AMZN WEB-DL DDP5 1 H 264-playWEB",
        "Rick and Morty S09E10 Field of Dreams REPACK 1080p AMZN WEB-DL DDP5 1 H 264-playWEB")]
    [InlineData(
        "Sugar 2024 S02E06 Cautionary Tale 1080p ATVP WEB-DL DDP5 1 Atmos H 264-FLUX[EZTVx.to]",
        "Sugar 2024 S02E06 Cautionary Tale 1080p ATVP WEB-DL DDP5 1 Atmos H 264-FLUX EZTV")]
    public void AFileLeftInTheIntakeFolderIsFoundByTheGrabThatStagedIt(string onDisk, string grabbed)
    {
        Assert.Equal(TitleMatcher.Release(grabbed), TitleMatcher.Release(onDisk));
    }

    /// <remarks>
    /// And a different release of the same episode is not it. The owner's
    /// Lioness S03E04 was grabbed twice — once as an executable wearing the
    /// name, once as CAKES — and matching on the episode rather than the
    /// release would hand the encode of one to the other.
    /// </remarks>
    [Fact]
    public void AnotherReleaseOfTheSameEpisodeIsNotAMatch()
    {
        Assert.NotEqual(
            TitleMatcher.Release("Lioness 2023 S03E04 1080p WEB h264-ETHEL.exe"),
            TitleMatcher.Release("lioness.2023.s03e04.1080p.web.h264-cakes[EZTVx.to]"));
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

        // A trailing word that is not a site is the release group and stays.
        Assert.NotEqual(
            TitleMatcher.Release("Silo S03E04 1080p WEB H264-CAKES"),
            TitleMatcher.Release("Silo S03E04 1080p WEB H264"));

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
