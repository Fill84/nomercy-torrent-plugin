using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// What the owner will accept, applied to names and to copies.
/// </summary>
/// <remarks>
/// <strong>H1.</strong> Every test here uses the real profile. 0.3.4's tests
/// for the seeder fault all stubbed it out with a fake chooser and passed
/// throughout, while not one indexer was ever asked.
/// </remarks>
public class ReleaseFilterTests
{
    /// <remarks>
    /// <strong>A1, the fault this whole plugin was rewritten for.</strong> A
    /// name is a name. It has no seeders, no size and no site, and asking it
    /// how many seeders it has answers nought — which is below every minimum,
    /// so every announcement was refused, the resolver was never reached and
    /// not one indexer was ever asked. The log said "searched 24 episodes,
    /// found nothing worth taking".
    /// </remarks>
    [Fact]
    public void ANameIsNeverJudgedOnSeeders()
    {
        Profile profile = new() { MinimumSeeders = 500 };

        Verdict verdict = new ReleaseFilter(profile).JudgeName(
            ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")),
            Episode("Silo", 3, 6),
            Blacklist.None);

        Assert.True(verdict.Accepted, verdict.Reason);

        // And the rule really is armed: the same profile refuses a copy for it.
        Assert.False(new ReleaseFilter(profile).JudgeCopy(Copy(seeders: 12), Blacklist.None).Accepted);
    }

    /// <remarks>
    /// A copy nobody is seeding is refused, and the reason names the site and
    /// the count. "Nothing worth taking" is what 0.3.4 said, and it is the
    /// sentence that made a whole release's worth of faults invisible.
    /// </remarks>
    [Fact]
    public void ACopyBelowTheMinimumIsRefusedWithTheSiteAndTheCount()
    {
        Verdict verdict = new ReleaseFilter(new() { MinimumSeeders = 2 })
            .JudgeCopy(Copy(seeders: 1, source: "LimeTorrents"), Blacklist.None);

        Assert.False(verdict.Accepted);
        Assert.Contains("LimeTorrents", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("1", verdict.Reason, StringComparison.Ordinal);
        Assert.Contains("2", verdict.Reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A site that does not publish a seeder count has not said nought. Judging
    /// a copy on a number nobody gave is the same category error as judging a
    /// name on one, and it would silently drop every source that leaves the
    /// count out.
    /// </remarks>
    [Fact]
    public void ACopyWhoseSeedersAreUnknownIsNotRefusedForHavingNone()
    {
        Verdict verdict = new ReleaseFilter(new() { MinimumSeeders = 2 })
            .JudgeCopy(Copy(seeders: null), Blacklist.None);

        Assert.True(verdict.Accepted, verdict.Reason);
    }

    /// <remarks>
    /// The title must be the show's. <em>A Bloody Lucky Day</em> contains
    /// <em>Lucky</em> and is a different programme; the capture carries
    /// <c>Silos / Silo (2023–)</c>, which is the same trap with a real row
    /// behind it.
    /// </remarks>
    [Fact]
    public void AReleaseForAnotherShowIsRefused()
    {
        Verdict verdict = Filter().JudgeName(
            ReleaseName.Parse(Real(
                "torrentbay.html",
                "torrentbay",
                "Silos / Silo (2023–) S03E06 [PLAI.EN.IT.MultiSub.1080p.H265.EAC3 5.1] [LeGo].mkv [mkv] [FIONA9]")),
            Episode("Silo", 3, 6),
            Blacklist.None);

        Assert.False(verdict.Accepted);
        Assert.Contains("Silo", verdict.Reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And the slot must be the episode's. A name for the right show and the
    /// wrong episode is the easiest wrong download there is.
    /// </remarks>
    [Fact]
    public void AReleaseForAnotherEpisodeIsRefused()
    {
        ReleaseName name = ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));

        Assert.True(Filter().JudgeName(name, Episode("Silo", 3, 6), Blacklist.None).Accepted);
        Assert.False(Filter().JudgeName(name, Episode("Silo", 3, 7), Blacklist.None).Accepted);
        Assert.False(Filter().JudgeName(name, Episode("Silo", 2, 6), Blacklist.None).Accepted);
    }

    /// <remarks>
    /// An anime episode is matched on its absolute number as well, because that
    /// is the only number half its releases carry.
    /// </remarks>
    [Fact]
    public void AnAnimeReleaseIsMatchedOnItsAbsoluteNumber()
    {
        ReleaseName name = ReleaseName.Parse(Real(
            "nyaa-absolute.xml",
            "torrent-rss",
            "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv"));

        Assert.True(Filter(new() { MaximumResolution = "1080p", Codec = "h265" })
            .JudgeName(name, Episode("One Piece", 21, 45, LibraryKind.Anime, absolute: 1172), Blacklist.None)
            .Accepted);

        Assert.False(Filter(new() { MaximumResolution = "1080p", Codec = "h265" })
            .JudgeName(name, Episode("One Piece", 21, 46, LibraryKind.Anime, absolute: 1173), Blacklist.None)
            .Accepted);
    }

    /// <remarks>
    /// Quality is one rung, not a ceiling. A ceiling reads as generous and
    /// behaves as a downgrade, because the 720p copy is usually posted first
    /// and would be taken every time.
    /// </remarks>
    [Fact]
    public void AResolutionOffTheRungIsRefusedInBothDirections()
    {
        Profile wants1080 = new() { MaximumResolution = "1080p" };

        Assert.True(Filter(wants1080).JudgeName(
            ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        Assert.False(Filter(wants1080).JudgeName(
            ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.720p.x264-FENiX")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        Assert.False(Filter(wants1080).JudgeName(
            ReleaseName.Parse(Real(
                "1337x.html",
                "1337x",
                "Silo.S03E06.The.Drive.2160p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.DV.HDR.H.265-G66.mkv")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);
    }

    /// <remarks>
    /// A release that does not say what resolution it is cannot be shown to be
    /// on the rung. It is refused for that reason and not for being 720p, which
    /// is a different sentence for the owner to read.
    /// </remarks>
    [Fact]
    public void AReleaseThatNamesNoResolutionIsRefused()
    {
        Verdict verdict = Filter().JudgeName(
            ReleaseName.Parse(Real("eztv.html", "eztv", "Silo S03E06 XviD-AFG")),
            Episode("Silo", 3, 6),
            Blacklist.None);

        Assert.False(verdict.Accepted);
        Assert.Contains("resolution", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The codec the owner asked for, and no other. <c>x265</c> is refused when
    /// the profile says h264 however good the copy is.
    /// </remarks>
    [Fact]
    public void AnotherCodecIsRefused()
    {
        Profile wantsH264 = new() { MaximumResolution = "1080p", Codec = "h264" };

        Assert.False(Filter(wantsH264).JudgeName(
            ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        Assert.True(Filter(wantsH264).JudgeName(
            ReleaseName.Parse(Real("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);
    }

    /// <remarks>
    /// An untagged release is where the unwanted codec hides, so it is refused
    /// when a codec was asked for — and only then. With no codec wanted the
    /// same rule would refuse most of what the feeds carry and leave the owner
    /// an empty queue with no reason given.
    /// </remarks>
    [Fact]
    public void AnUntaggedReleaseIsRefusedOnlyWhenACodecWasAskedFor()
    {
        // A real row that names its resolution and no codec at all.
        ReleaseName untagged = ReleaseName.Parse(Real(
            "torrentbay.html",
            "torrentbay",
            "Silo S03E06 (EN)[WEB-DL][1080p]"));

        Assert.Null(untagged.Codec);

        Assert.False(Filter(new() { MaximumResolution = "1080p", Codec = "h265" })
            .JudgeName(untagged, Episode("Silo", 3, 6), Blacklist.None).Accepted);

        // And taken by a profile that wants a codec but does not insist on the
        // tag, which is what RequireCodecTag being off means.
        Assert.True(Filter(new() { MaximumResolution = "1080p", Codec = "h265", RequireCodecTag = false })
            .JudgeName(untagged, Episode("Silo", 3, 6), Blacklist.None).Accepted);

        // A profile wanting no codec in particular takes it. English only is
        // off here on purpose: this release is MULTI, and that is a language
        // question rather than a codec one.
        Assert.True(Filter(new() { MaximumResolution = "1080p", Codec = Profile.AnyCodec, EnglishOnly = false })
            .JudgeName(
                ReleaseName.Parse(Real("limetorrents.html", "site", "Silo S03E06 MULTI 1080p WEB H264-HiggsBoson")),
                Episode("Silo", 3, 6),
                Blacklist.None)
            .Accepted);
    }

    /// <remarks>
    /// English only means English only. A release in German says so and nothing
    /// else; one carrying both Italian and English audio is a release with
    /// English in it and is taken.
    /// </remarks>
    [Fact]
    public void AReleaseInAnotherLanguageIsRefusedAndOneCarryingEnglishIsNot()
    {
        Profile english = new() { MaximumResolution = "1080p", EnglishOnly = true };

        Assert.False(Filter(english).JudgeName(
            ReleaseName.Parse(Real("predb.xml", "rss", "Silo.S03E06.GERMAN.DL.1080p.WEB.h264-SAUERKRAUT")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        // And a release carrying both is still refused. It says ITA.ENG: the
        // English audio is in there with the Italian, and the owner asked for
        // English. This asserted the opposite until 22 August 2026, when a
        // MULTI release was taken for an episode whose plain one was sitting
        // beside it.
        Assert.False(Filter(english).JudgeName(
            ReleaseName.Parse(Real(
                "1337x.html",
                "1337x",
                "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.H.265-G66.mkv")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        // And the plain one beside it is taken.
        Assert.True(Filter(english).JudgeName(
            ReleaseName.Parse(Real("limetorrents.html", "site", "Silo S03E06 1080p WEB H264-CAKES")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);

        // And with the rule off, the German one is as good as any other.
        Assert.True(Filter(new() { MaximumResolution = "1080p", EnglishOnly = false }).JudgeName(
            ReleaseName.Parse(Real("predb.xml", "rss", "Silo.S03E06.GERMAN.DL.1080p.WEB.h264-SAUERKRAUT")),
            Episode("Silo", 3, 6),
            Blacklist.None).Accepted);
    }

    /// <remarks>
    /// A forbidden term is forbidden wherever it appears in the name, which is
    /// what makes it the rule that refuses a release group as well as a word.
    /// </remarks>
    [Fact]
    public void AForbiddenTermRefusesTheNameItAppearsIn()
    {
        Profile profile = new() { MaximumResolution = "1080p", ExcludeTerms = ["ELiTE"] };

        Verdict verdict = Filter(profile).JudgeName(
            ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")),
            Episode("Silo", 3, 6),
            Blacklist.None);

        Assert.False(verdict.Accepted);
        Assert.Contains("ELiTE", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A blacklisted title is refused as a name, and a blacklisted hash as a
    /// copy — the only rule in the table that applies to both, because a
    /// torrent that failed to download is worth refusing under whichever name
    /// it is offered next.
    /// </remarks>
    [Fact]
    public void ABlacklistedTitleOrHashIsRefused()
    {
        ReleaseName name = ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));

        Assert.False(Filter()
            .JudgeName(name, Episode("Silo", 3, 6), Blacklist.Of(Blacklist.KeyOf("Silo.S03E06.1080p.x265-ELiTE")))
            .Accepted);

        Assert.False(Filter()
            .JudgeCopy(Copy(seeders: 40, hash: "92D8A3F6864911EF292B4BE0DD5286406396D2B3"), Blacklist.Of("92D8A3F6864911EF292B4BE0DD5286406396D2B3"))
            .Accepted);
    }

    /// <remarks>
    /// A pack is a name for a season, and it is refused outright when the owner
    /// does not want packs. Whether a wanted pack is worth its bytes is a
    /// different question, asked of the gaps rather than of the name.
    /// </remarks>
    [Fact]
    public void ASeasonPackIsRefusedWhenPacksAreNotAllowed()
    {
        ReleaseName pack = ReleaseName.Parse(Real(
            "nyaa-diacritic.xml",
            "torrent-rss",
            "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)"));

        TrackedEpisode episode = Episode("Pokemon Master Quest", 5, 3);

        Assert.False(Filter(new() { MaximumResolution = "1080p", EnglishOnly = false, AllowSeasonPacks = false })
            .JudgeName(pack, episode, Blacklist.None).Accepted);

        Assert.True(Filter(new() { MaximumResolution = "1080p", EnglishOnly = false, AllowSeasonPacks = true })
            .JudgeName(pack, episode, Blacklist.None).Accepted);
    }

    /// <remarks>
    /// <para>
    /// <strong>Only a video file.</strong> A release whose name ends in a file
    /// type has to end in a video one. On 22 August 2026 the owner's server
    /// grabbed <c>Lioness 2023 S03E02 1080p WEB h264-ETHEL.exe</c> — 1.2 GB of
    /// executable named after an episode — and nothing in the chain looked at
    /// it, because the only rule that knew about file types lived in one site's
    /// reader and only fired when that site wrote the type as a separate word.
    /// </para>
    /// <para>
    /// The name is a claim and not the truth, which is why the same rule is
    /// applied again to the torrent's own contents. This one saves the grab.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("Silo.S03E06.1080p.WEB.H264-CAKES", true)]
    [InlineData("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", true)]
    [InlineData("Silo S03E06 1080p WEB H264-CAKES mkv", true)]
    [InlineData("Silo.S03E06.1080p.WEB.H264-CAKES.exe", false)]
    [InlineData("Silo S03E06 1080p WEB H264-CAKES exe", false)]
    [InlineData("Silo.S03E06.1080p.WEB.H264-CAKES.rar", false)]
    [InlineData("Silo.S03E06.1080p.WEB.H264-CAKES.iso", false)]
    public void ANameThatCarriesAFileTypeHasToCarryAVideoOne(string title, bool accepted)
    {
        Verdict verdict = Filter().JudgeName(
            ReleaseName.Parse(title),
            Episode("Silo", 3, 6),
            Blacklist.None);

        Assert.Equal(accepted, verdict.Accepted);
    }

    /// <remarks>
    /// The release group is not a file type. Reading it as one takes the group
    /// off the name or refuses the release outright, and both were measured
    /// against real captures: <c>Greek S01E01 HR HDTV XviD-2HD</c> disappeared
    /// the first time this was written by taking the last word blindly.
    /// </remarks>
    [Theory]
    [InlineData("Greek S01E01 HR HDTV XviD-2HD")]
    [InlineData("Silo.S03E06.1080p.WEB.H264-FQM")]
    [InlineData("Silo.S03E06.PROPER.1080p.WEB.H264-NTb")]
    public void AReleaseGroupIsNotAFileType(string title)
    {
        Assert.Null(TitleMatcher.FileType(title));
    }

    private static ReleaseFilter Filter(Profile? profile = null)
    {
        return new(profile ?? new() { MaximumResolution = "1080p" });
    }

    private static ReleaseCopy Copy(int? seeders, string source = "LimeTorrents", string? hash = null)
    {
        return new("Silo.S03E06.1080p.x265-ELiTE", source, 35, hash, null, null, seeders, null);
    }

    private static TrackedEpisode Episode(
        string show,
        int season,
        int number,
        LibraryKind kind = LibraryKind.Television,
        int? absolute = null)
    {
        return new(
            new(show.GetHashCode(StringComparison.Ordinal), season, number),
            show,
            null,
            kind,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing,
            absolute);
    }

    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return name;
    }
}
