using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Naming;

/// <summary>
/// Parsing a release name, against names sites really printed.
/// </summary>
/// <remarks>
/// <strong>H3.</strong> 0.3.4's parser was tested against hand-written samples
/// and avoided every real case: a show called <em>Greek</em> read as a
/// Greek-language release, a diacritic tokenised into fragments, and
/// <c>[eztv.re]</c> stuck on the end of every title. Every name in this file is
/// one a reader really read off a capture in <c>tests/fixtures/</c>, and
/// <see cref="Real"/> proves it rather than trusting the quotation.
/// </remarks>
public class ReleaseNameTests
{
    /// <remarks>
    /// The title is what comes before the season tag, and the separators a site
    /// writes it with are not part of it.
    /// </remarks>
    [Fact]
    public void TheTitleIsWhatComesBeforeTheSeasonTag()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "1337x.html",
            "1337x",
            "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.H.265-G66.mkv"));

        Assert.Equal("Silo", parsed.Title);
        Assert.Equal(3, parsed.Season);
        Assert.Equal(6, parsed.Episode);
    }

    /// <remarks>
    /// <strong>H3, the case it is named for.</strong> <em>Greek</em> is a
    /// programme. A parser that reads the word as a language loses the title
    /// altogether and the episode is never found — and the page it came off
    /// carries both kinds, since half its rows are somebody else's show with
    /// Greek subtitles.
    /// </remarks>
    [Fact]
    public void AShowCalledGreekIsATitleAndNotALanguage()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "torrentdownloads-greek.html",
            "torrentdownloads",
            "Greek S01E01 HR HDTV XviD-2HD"));

        Assert.Equal("Greek", parsed.Title);
        Assert.Equal(1, parsed.Season);
        Assert.Equal(1, parsed.Episode);
        Assert.Empty(parsed.Languages);
        Assert.Equal("xvid", parsed.Codec);
        Assert.Equal("2HD", parsed.Group);
    }

    /// <remarks>
    /// The quality is one rung and it is written differently everywhere: bare
    /// on a scene name, in brackets on an anime one, and in whichever case the
    /// group felt like.
    /// </remarks>
    [Theory]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.720p.x264-FENiX", "720p")]
    [InlineData("limetorrents.html", "site", "Silo S03E06 The Drive 1080P ATVP WEB-DL DDP5 1 Atmos X265 POOTLED", "1080p")]
    [InlineData("nyaa-subsplease.xml", "torrent-rss", "[SubsPlease] Rilakkuma - 20 (1080p) [A8302A8E].mkv", "1080p")]
    public void TheQualityIsTakenHoweverTheSiteWroteIt(string fixture, string reader, string name, string resolution)
    {
        Assert.Equal(resolution, ReleaseName.Parse(Real(fixture, reader, name)).Resolution);
    }

    /// <remarks>
    /// <c>H.265</c> has a dot inside it, and a site that turned the dots into
    /// spaces leaves a bare <c>264</c> with nothing in front of it. Both are the
    /// codec; a name whose codec is not read is one the codec rule cannot judge,
    /// and an untagged release is where the unwanted codec hides.
    /// </remarks>
    [Theory]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.H.265-G66.mkv", "h265")]
    [InlineData("eztv.html", "eztv", "Silo S03E06 The Drive 720p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB", "h264")]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE", "h265")]
    [InlineData("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES", "h264")]
    [InlineData("eztv.html", "eztv", "Silo S03E06 XviD-AFG", "xvid")]
    [InlineData("eztv.html", "eztv", "Silo S03E06 1080p HEVC x265-MeGusta", "h265")]
    public void ACodecIsReadWithOrWithoutItsPrefixAndWithItsDot(string fixture, string reader, string name, string codec)
    {
        ReleaseName parsed = ReleaseName.Parse(Real(fixture, reader, name));

        Assert.Equal(codec, parsed.Codec);
        Assert.True(parsed.HasCodecTag);
    }

    /// <remarks>
    /// A scene title is full of dashes and the group is after the last one. It
    /// contains no dot, which is what tells a group from the tail of a name
    /// that happens to follow a dash.
    /// </remarks>
    [Theory]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.720p.x264-FENiX", "FENiX")]
    [InlineData("eztv.html", "eztv", "Silo S03E06 The Drive 720p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB", "playWEB")]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.Giro.in.auto.ITA.ENG.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-MeM.GP.mkv", null)]
    // The last dash is the one in WEB-DL, and what follows it is half the name.
    [InlineData("limetorrents.html", "site", "Silo S03E06 The Drive 1080P ATVP WEB-DL DDP5 1 Atmos X265 POOTLED", null)]
    // And here it is the dash before the group, but the group is followed by
    // two more bracketed words, so the name does not end where a group would.
    [InlineData("nyaa.xml", "torrent-rss", "Frieren - Beyond Journey's End S01E13 VOSTFR 1080p WEB x264 AAC -Tsundere-Raws (CR) (Sousou no Frieren)", null)]
    public void TheGroupIsAfterTheLastDashAndCarriesNoDot(string fixture, string reader, string name, string? group)
    {
        Assert.Equal(group, ReleaseName.Parse(Real(fixture, reader, name)).Group);
    }

    /// <remarks>
    /// The languages a name claims, and only those. Everything else in a name
    /// is a word, including the ones that are also the names of languages.
    /// </remarks>
    [Fact]
    public void ALanguageIsReadOnlyFromTheWordsThatAreOne()
    {
        Assert.Equal(
            ["multi"],
            ReleaseName.Parse(Real(
                "limetorrents.html",
                "site",
                "Silo S03E06 MULTI 1080p WEB H264-HiggsBoson")).Languages);

        Assert.Equal(
            ["vostfr"],
            ReleaseName.Parse(Real(
                "nyaa.xml",
                "torrent-rss",
                "Frieren - Beyond Journey's End S01E13 VOSTFR 1080p WEB x264 AAC -Tsundere-Raws (CR) (Sousou no Frieren)")).Languages);

        Assert.Equal(
            ["dual audio"],
            ReleaseName.Parse(Real(
                "nyaa.xml",
                "torrent-rss",
                "[ZeroBuild] Frieren: Beyond Journey's End - S01E13 (WEB 1080p HEVC 10-bit E-AC-3) [Dual Audio] (Sousou no Frieren)")).Languages);

        // Subtitles in several languages are not the release being in several
        // languages, and this row claims neither. Reading it as MULTi would
        // have the English-only rule refuse an English release.
        Assert.Empty(ReleaseName.Parse(Real(
            "nyaa.xml",
            "torrent-rss",
            "[Judas] Sousou no Frieren (Frieren: Beyond Journey's End) - S01E13 [1080p][HEVC x265 10bit][Multi-Subs] (Weekly)")).Languages);
    }

    /// <remarks>
    /// The languages the captures really claim, which is a longer list than
    /// four. A release in German says <c>GERMAN</c> and nothing else, and a
    /// profile that only knows <c>MULTi</c> and <c>VOSTFR</c> accepts it as
    /// English — which is the English-only rule quietly not working.
    /// </remarks>
    [Theory]
    [InlineData("predb.xml", "rss", "Silo.S03E06.GERMAN.WEBRiP.x264-AVTOMAT", "german")]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.H.265-G66.mkv", "italian")]
    [InlineData("1337x.html", "1337x", "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.H.265-G66.mkv", "english")]
    [InlineData("nyaa-diacritic.xml", "torrent-rss", "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)", "french")]
    [InlineData("torrentdownloads-greek.html", "torrentdownloads", "Greek S01e01 Swesub Hdtv Xvid D_s avi", "swedish")]
    [InlineData("torrentdownloads-greek.html", "torrentdownloads", "Greek S01e01 2007 Spanish Dvd Xvid [www Torrentmas Com]", "spanish")]
    [InlineData("nyaa-version.xml", "torrent-rss", "[Erai-raws] Spy x Family Part 2 - 01 ~ 13 (v2) [480p][BATCH][Multiple Subtitle] [ENG][POR-BR][SPA-LA][SPA][ARA][FRE][GER][ITA][RUS]", "russian")]
    public void ALanguageIsReadWhereverTheCaptureClaimsOne(string fixture, string reader, string name, string language)
    {
        Assert.Contains(language, ReleaseName.Parse(Real(fixture, reader, name)).Languages);
    }

    /// <remarks>
    /// <strong>H3, still.</strong> <em>Greek</em> is a programme and Greek is
    /// not in the vocabulary — the page this name came off carries a dozen rows
    /// with Greek subtitles as well, and both have to read correctly.
    /// </remarks>
    [Fact]
    public void TheLanguageVocabularyStillHasNoGreekInIt()
    {
        Assert.Empty(ReleaseName.Parse(Real(
            "torrentdownloads-greek.html",
            "torrentdownloads",
            "Greek S01E01 HR HDTV XviD-2HD")).Languages);

        Assert.Empty(ReleaseName.Parse(Real(
            "torrentdownloads-greek.html",
            "torrentdownloads",
            "Fringe S01e01 Hdtv Xvid notv Greek Subs")).Languages);
    }

    /// <remarks>
    /// A season with no episode is the whole season, and it answers for every
    /// gap in it. Reading it as an episode would have the plugin download a
    /// season pack believing it was episode nought.
    /// </remarks>
    [Fact]
    public void ASeasonWithNoEpisodeIsAPack()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-diacritic.xml",
            "torrent-rss",
            "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)"));

        Assert.Equal(5, parsed.Season);
        Assert.Null(parsed.Episode);
        Assert.True(parsed.IsPack);

        // The group is inside the leading brackets even on a name that is
        // otherwise scene-shaped.
        Assert.Equal("T3KASHi", parsed.Group);
    }

    /// <remarks>
    /// The anime grammar: the group is in the leading brackets, the title is
    /// what follows them, and the number after the separator is the episode's
    /// own — counted from the start of the programme rather than of a season.
    /// </remarks>
    [Fact]
    public void AnAnimeNameIsAGroupATitleAndAnAbsoluteNumber()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-subsplease.xml",
            "torrent-rss",
            "[SubsPlease] Mairimashita! Iruma-kun S4 - 19 (1080p) [2ADA8299].mkv"));

        Assert.Equal("SubsPlease", parsed.Group);
        Assert.Equal("Mairimashita! Iruma-kun S4", parsed.Title);
        Assert.Equal(19, parsed.Absolute);
        Assert.Equal("1080p", parsed.Resolution);
        Assert.Null(parsed.Episode);
    }

    /// <remarks>
    /// <c>137</c> is an episode and <c>1080</c> is not. The only thing that
    /// tells them apart is the <c>p</c> after one of them, and a parser that
    /// takes the first number it sees calls every anime release episode 1080.
    /// </remarks>
    [Fact]
    public void ABareNumberIsAnEpisodeOnlyWhenNoPFollowsIt()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[Naruto-Kun.Hu] One Piece (Elbaf arc) - 1172 [1080p].mkv"));

        Assert.Equal(1172, parsed.Absolute);
        Assert.Equal("1080p", parsed.Resolution);

        // A name whose only number is its resolution has no episode in it at
        // all. Eight rows of the Pokémon capture are shaped like this one.
        Assert.Null(ReleaseName.Parse(Real(
            "nyaa-diacritic.xml",
            "torrent-rss",
            "[TardS] Pokemon Inai Inai Baa! (WEB 1080p)")).Absolute);

        // Written by hand, and the only name in this file that is: no captured
        // page carries a title ending in the separator with the resolution
        // straight after it, and this is the shape the rule in
        // docs/04-domain.md is written against. Setting aside the rule that a
        // parser is only tested against captures, because the alternative is a
        // rule with nothing at all holding it in place.
        Assert.Null(ReleaseName.Parse("Some Show - 1080p WEB x264-GROUP").Absolute);

        // The same exception for the other half of the same sentence in that
        // document: the separator has spaces around it. No captured name
        // without a season tag carries a dash against a digit, and this is
        // what stops the 3 of E-AC-3 being episode three.
        Assert.Null(ReleaseName.Parse("Some Show E-AC-3 (WEB 1080p)").Absolute);
    }

    /// <remarks>
    /// The other way the number is written, which no document mentions and the
    /// Nyaa capture is full of: <c>EP1173</c>, with no separator anywhere in
    /// the name. A release whose episode is not read answers for nothing, and
    /// One Piece is posted this way every week.
    /// </remarks>
    [Fact]
    public void AnEpisodeTaggedEpIsReadTheSameAsOneAfterASeparator()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[ToonsHub] One Piece EP1173 1080p NF WEB-DL AAC2.0 H.264 (Multi-Subs)"));

        Assert.Equal(1173, parsed.Absolute);
        Assert.Equal("One Piece", parsed.Title);
        Assert.Equal("1080p", parsed.Resolution);
    }

    /// <remarks>
    /// A <c>v2</c> supersedes the <c>v1</c> of the same episode. It is written
    /// against the number on one site and on its own further along on another,
    /// and a name with neither is version one.
    /// </remarks>
    [Fact]
    public void AVersionIsReadWhereverItIsWrittenAndIsOneWhenItIsNotWritten()
    {
        ReleaseName attached = ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv"));

        Assert.Equal(1172, attached.Absolute);
        Assert.Equal(2, attached.Version);

        // And the codec is still read, from inside a bracket that also carries
        // a dash and a number of its own.
        Assert.Equal("h265", attached.Codec);

        ReleaseName standalone = ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[A&C] One Piece - Movie 01 (BD 1080p HEVC) [Multi-Subs] [v2]"));

        Assert.Equal(2, standalone.Version);

        Assert.Equal(
            1,
            ReleaseName.Parse(Real(
                "nyaa-subsplease.xml",
                "torrent-rss",
                "[SubsPlease] Rilakkuma - 20 (1080p) [A8302A8E].mkv")).Version);
    }

    /// <remarks>
    /// A batch is a pack of every episode it names, and it answers for each of
    /// them. The site writes the range with spaces around the tilde, which no
    /// document mentions and every one of these rows does.
    /// </remarks>
    [Fact]
    public void AnAnimeBatchIsAPackOfEveryEpisodeItCovers()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-version.xml",
            "torrent-rss",
            "[Erai-raws] Fullmetal Alchemist: Brotherhood - 01 ~ 64 (V2) [1080p NF WEB-DL AVC AAC][MultiSub] [BATCH]"));

        Assert.True(parsed.IsPack);
        Assert.Equal(1, parsed.Absolute);
        Assert.Equal(64, parsed.LastAbsolute);
        Assert.Equal("Fullmetal Alchemist: Brotherhood", parsed.Title);
        Assert.Equal(2, parsed.Version);
    }

    /// <remarks>
    /// <para>
    /// The other word a batch is written with. A tilde range says what it
    /// covers and this does not — it is one name for a whole programme — but it
    /// is a pack all the same, and a client that read it as one episode would
    /// take a saga and file it as episode nothing.
    /// </para>
    /// <para>
    /// Both rows are really on the captured page, which is the point: nobody
    /// writes a fansub batch the way a document would.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("[ColdFusion] Pokemon (English Dub) Complete Ash Ketchum Saga & Bonus")]
    [InlineData("[javieracdc] Pokemon Complete Music Collection (Gen 1-9) [FLAC]")]
    public void TheWordCompleteMakesAPackJustAsBatchDoes(string name)
    {
        Assert.True(ReleaseName.Parse(Real("nyaa-diacritic.xml", "torrent-rss", name)).IsPack);
    }

    /// <remarks>
    /// <strong>H3.</strong> A diacritic is a letter. 0.3.4 tokenised it into
    /// fragments and the show it belonged to was never matched again.
    /// </remarks>
    [Fact]
    public void ADiacriticSurvivesTheTitle()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa-diacritic.xml",
            "torrent-rss",
            // The page writes two spaces after the number; the reader collapses
            // runs of space, so this is the name as the plugin really sees it.
            "Pokémon Horizons: The Series - 101 [English Dub][1080p][NF] ( Pokemon (2023) )"));

        Assert.Equal("Pokémon Horizons: The Series", parsed.Title);
        Assert.Equal(101, parsed.Absolute);
    }

    /// <remarks>
    /// Anime is posted scene-styled as often as not, and a name with a season
    /// tag is read by the season tag whoever posted it.
    /// </remarks>
    [Fact]
    public void SceneStyledAnimeIsReadAsScene()
    {
        ReleaseName parsed = ReleaseName.Parse(Real(
            "nyaa.xml",
            "torrent-rss",
            "Frieren.Beyond.Journey.s.End.S01E13.MULTi.1080p.WEB.x264-T3KASHi"));

        Assert.Equal(1, parsed.Season);
        Assert.Equal(13, parsed.Episode);
        Assert.Null(parsed.Absolute);
        Assert.Equal("T3KASHi", parsed.Group);
    }

    /// <remarks>
    /// Every name off every captured page, through the parser, with the one
    /// thing that can be checked against the name itself: a name carrying a
    /// season tag parses to that season and that episode. Eight hundred names
    /// from seventeen sites, and any of them that throws fails here.
    /// </remarks>
    [Theory]
    [MemberData(nameof(EveryCapture))]
    public void EveryNameOnEveryCapturedPageKeepsTheSlotItCarries(string fixture, string reader)
    {
        string[] names = [.. Names(fixture, reader)];

        Assert.NotEmpty(names);

        foreach (string name in names)
        {
            ReleaseName parsed = ReleaseName.Parse(name);

            Assert.Equal(name, parsed.Original);

            // Over eight hundred real names, and not one of them has its
            // resolution read as its episode. The number is matched as a whole
            // one: episode 20 of Rilakkuma is a real row printed beside 720p,
            // and a substring check calls that a fault when it is not one.
            if (parsed.Absolute is int number)
            {
                Assert.False(
                    Regex.IsMatch(name, $@"\b{number}p\b", RegexOptions.IgnoreCase),
                    $"{name} was read as episode {number}, which is its resolution.");
            }

            Match slot = Slot.Match(name);

            if (!slot.Success)
            {
                continue;
            }

            Assert.Equal(int.Parse(slot.Groups[1].Value), parsed.Season);
            Assert.Equal(int.Parse(slot.Groups[2].Value), parsed.Episode);
        }
    }

    /// <summary>The season tag, read the same way the test and the parser must.</summary>
    private static readonly Regex Slot = new(@"\bS(\d{1,2})E(\d{1,4})", RegexOptions.IgnoreCase);

    public static TheoryData<string, string> EveryCapture()
    {
        TheoryData<string, string> data = [];

        foreach ((string fixture, string reader) in Captures)
        {
            data.Add(fixture, reader);
        }

        return data;
    }

    /// <summary>Every captured page, and the reader that reads it.</summary>
    private static readonly (string Fixture, string Reader)[] Captures =
    [
        ("1337x.html", "1337x"),
        ("eztv.html", "eztv"),
        ("kickasstorrents.html", "kickass"),
        ("kickasstorrents-full-name.html", "kickass"),
        ("limetorrents.html", "site"),
        ("torrentbay.html", "torrentbay"),
        ("torrentdownloads.html", "torrentdownloads"),
        ("torrentdownloads-greek.html", "torrentdownloads"),
        ("torrentfunk.html", "torrentfunk"),
        ("torrentgalaxy.html", "torrentgalaxy"),
        ("torrentz2.html", "torrentz2"),
        ("nyaa.xml", "torrent-rss"),
        ("nyaa-absolute.xml", "torrent-rss"),
        ("nyaa-version.xml", "torrent-rss"),
        ("nyaa-subsplease.xml", "torrent-rss"),
        ("nyaa-diacritic.xml", "torrent-rss"),
        ("predb.xml", "rss"),
        ("scenesource.xml", "rss"),
        ("srrdb.xml", "rss"),
        ("srrdb-search.json", "srrdb"),
        ("the-pirate-bay.json", "apibay"),
        ("eztv-latest.json", "eztv-api"),
    ];

    /// <summary>
    /// A name a site really printed, proven against the capture it came from.
    /// </summary>
    /// <remarks>
    /// The name is quoted in the test because a test nobody can read is worth
    /// little, and then checked against the page so the quotation cannot drift
    /// into something no site ever sent. That drift is exactly H3: a sample
    /// that started real and was tidied until it parsed.
    /// </remarks>
    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Names(fixture, reader));

        return name;
    }

    private static readonly Dictionary<string, IReadOnlyList<string>> Read = [];
    private static readonly Lock Reading = new();

    private static IReadOnlyList<string> Names(string fixture, string reader)
    {
        lock (Reading)
        {
            if (Read.TryGetValue(fixture, out IReadOnlyList<string>? names))
            {
                return names;
            }

            // The address only has to be the site's; nothing here follows a
            // link, and a relative one in the page needs somewhere to hang off.
            names =
            [
                .. Readers.Shipped().Named(reader)!
                    .Read(File.ReadAllText(Path.Combine(Fixtures, fixture)), new("https://capture.invalid/"))
                    .Select(row => row.Title),
            ];

            Read[fixture] = names;

            return names;
        }
    }

    private static string Fixtures
    {
        get
        {
            DirectoryInfo? directory = new(AppContext.BaseDirectory);

            while (directory is not null
                   && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "tests", "fixtures");
        }
    }
}
