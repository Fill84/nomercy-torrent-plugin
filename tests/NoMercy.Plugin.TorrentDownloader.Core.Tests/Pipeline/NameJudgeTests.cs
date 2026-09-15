using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// A release name judged against the settings of its show, with its library's counted in.
/// </summary>
/// <remarks>
/// <c>docs/specs/release-names.md</c> § Choosing the release name and <c>show-list.md</c> § The settings of
/// a show. Every name here is one a site really published, off a captured page.
/// </remarks>
public sealed class NameJudgeTests
{
    private static readonly TrackedEpisode SiloSix = Episode("Silo", 3, 6);

    /// <remarks>
    /// Its resolution is the show's quality and its codec is the show's codec. A name that does not say
    /// which codec it is cannot be shown to be the one asked for; with codec <c>any</c> that question is
    /// not asked. A show with no quality on it or its library has no resolution to judge against.
    /// </remarks>
    [Fact]
    public void ANameIsTakenOnlyWithTheShowsQualityAndCodec()
    {
        EffectiveSettings h265 = Settings(quality: "1080p", codec: "h265");

        Assert.True(Judge(h265, Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")).Accepted);

        Verdict otherCodec = Judge(h265, Real("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES"));
        Assert.False(otherCodec.Accepted);
        Assert.Contains("h264", otherCodec.Reason, StringComparison.OrdinalIgnoreCase);

        Verdict otherQuality = Judge(h265, Real("1337x.html", "1337x", "Silo.S03E06.720p.x264-FENiX"));
        Assert.False(otherQuality.Accepted);
        Assert.Contains("720p", otherQuality.Reason, StringComparison.Ordinal);

        string untagged = Real("torrentbay.html", "torrentbay", "Silo S03E06 (EN)[WEB-DL][1080p]");
        Verdict notSaying = Judge(h265, untagged);
        Assert.False(notSaying.Accepted);

        // Refused for not saying, which the owner reads differently from being the wrong codec.
        Assert.Contains("does not say which codec", notSaying.Reason, StringComparison.Ordinal);
        Assert.True(Judge(Settings(quality: "1080p", codec: LibraryPreferences.AnyCodec), untagged).Accepted);

        Verdict noQuality = Judge(Settings(quality: null), Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));
        Assert.False(noQuality.Accepted);
        Assert.Contains("quality", noQuality.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>A downloaded release name carries every must tag, all of them.</remarks>
    [Fact]
    public void ANameMissingOneMustIsRefused()
    {
        EffectiveSettings musts = Settings(quality: "1080p", musts: ["WEB", "NTb"]);

        Verdict oneMissing = Judge(musts, Real("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES"));

        Assert.False(oneMissing.Accepted);
        Assert.Contains("NTb", oneMissing.Reason, StringComparison.Ordinal);

        Assert.True(Judge(musts, Real("limetorrents.html", "site", "Silo S03E06 The Drive 1080p ATVP WEB-DL DDP5 1 H 264-NTb")).Accepted);
    }

    /// <remarks>
    /// A release name carrying a forbidden tag is never downloaded, whatever else it carries: here it
    /// carries the must and a wish besides.
    /// </remarks>
    [Fact]
    public void ANameCarryingOneForbiddenIsRefusedWhateverElseItCarries()
    {
        EffectiveSettings settings = Settings(quality: "1080p", musts: ["WEB"], wishes: ["DL"], forbidden: ["DUAL"]);

        Verdict dual = Judge(settings, Real("torrentz2.html", "torrentz2", "Silo.S03E06.1080p.WEB-DL.DUAL.5.1"));

        Assert.False(dual.Accepted);
        Assert.Contains("DUAL", dual.Reason, StringComparison.Ordinal);

        Assert.True(Judge(settings, Real("limetorrents.html", "site", "Silo S03E06 The Drive 1080p ATVP WEB-DL DDP5 1 H 264-NTb")).Accepted);
    }

    /// <remarks>
    /// A tag is a word of the name, whatever the name puts between its words, and never part of a longer
    /// word: <c>WEB</c> forbidden refuses <c>ATVP WEB-DL</c> and leaves <c>WEBRip</c>, and <c>HDR</c> is not
    /// in <c>HDR10Plus</c>. Case is set aside, as it is for a title.
    /// </remarks>
    [Fact]
    public void ATagIsAWordOfTheNameAndNotPartOfAWord()
    {
        EffectiveSettings noWeb = Settings(quality: "2160p", forbidden: ["web"]);

        Assert.False(Judge(noWeb, Real("torrentz2.html", "torrentz2", "Silo S03E06 The Drive 2160p ATVP WEB-DL DDP5 1 Atmos DV HDR H 265-FLUX")).Accepted);
        Assert.True(Judge(noWeb, Real("torrentz2.html", "torrentz2", "Silo.S03E06.2160p.HDR10Plus.DV.WEBRip.6CH.x265.HEVC-PSA.mkv")).Accepted);

        Assert.True(NameJudge.Carries("Silo.S03E06.1080p.WEB-DL.DUAL.5.1", "DUAL"));
        Assert.True(NameJudge.Carries("Silo S03E06 The Drive 1080p ATVP WEB-DL DDP5 1 H 264-NTb", "WEB-DL"));
        Assert.False(NameJudge.Carries("Silo.S03E06.2160p.HDR10Plus.DV.WEBRip.6CH.x265.HEVC-PSA.mkv", "HDR"));
        Assert.False(NameJudge.Carries("Silo S03E06 The Drive 720p ATVP WEB-DL DDP5 1 Atmos H 264-playWEB", "play"));
    }

    /// <remarks>A wish decides no refusal: a name carrying none of them is judged exactly as one carrying all.</remarks>
    [Fact]
    public void AWishIsNeverAReasonToRefuse()
    {
        Assert.True(Judge(Settings(quality: "1080p", wishes: ["DUAL", "NTb"]), Real("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES")).Accepted);
    }

    /// <remarks>
    /// <strong>A1, the fault this whole plugin was rewritten for.</strong> A name has no seeders, no size and
    /// no site. 0.3.4 asked it how many seeders it had, got nought, and refused every announcement before an
    /// indexer was ever asked. No setting carries a seeder threshold, on a name or a copy.
    /// </remarks>
    [Fact]
    public void ANameIsNeverJudgedOnSeeders()
    {
        Assert.True(Judge(Settings(quality: "1080p"), Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")).Accepted);
    }

    /// <remarks>
    /// The owner's decision of 12 September 2026: there is no threshold, download what is found. A copy
    /// nobody is serving still starts, and one whose site gives no count has not said nought.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(null)]
    public void ACopyIsNeverRefusedForItsSeeders(int? seeders)
    {
        Assert.True(NameJudge.JudgeCopy(Copy(seeders), Blacklist.None).Accepted);
    }

    /// <remarks>
    /// The title must be the show's. The capture carries <c>Silos / Silo (2023–)</c>, which contains Silo and
    /// is a different programme's row.
    /// </remarks>
    [Fact]
    public void AReleaseForAnotherShowIsRefused()
    {
        Verdict verdict = Judge(
            Settings(quality: "1080p"),
            Real("torrentbay.html", "torrentbay", "Silos / Silo (2023–) S03E06 [PLAI.EN.IT.MultiSub.1080p.H265.EAC3 5.1] [LeGo].mkv [mkv] [FIONA9]"));

        Assert.False(verdict.Accepted);
        Assert.Contains("Silo", verdict.Reason, StringComparison.Ordinal);
    }

    /// <remarks>A name for the right show and the wrong episode is the easiest wrong download there is.</remarks>
    [Fact]
    public void AReleaseForAnotherEpisodeIsRefused()
    {
        ReleaseName name = ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));
        NameJudge judge = new(Settings(quality: "1080p"));

        Assert.True(judge.JudgeName(name, Episode("Silo", 3, 6), Blacklist.None).Accepted);
        Assert.False(judge.JudgeName(name, Episode("Silo", 3, 7), Blacklist.None).Accepted);
        Assert.False(judge.JudgeName(name, Episode("Silo", 2, 6), Blacklist.None).Accepted);
    }

    /// <remarks>
    /// An anime row is matched on its absolute number as well, because that is the only number half its
    /// releases carry. Rows are still judged here until <c>S13-07</c>.
    /// </remarks>
    [Fact]
    public void AnAnimeReleaseIsMatchedOnItsAbsoluteNumber()
    {
        ReleaseName name = ReleaseName.Parse(Real("nyaa-absolute.xml", "torrent-rss", "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv"));
        NameJudge judge = new(Settings(quality: "1080p", codec: "h265"));

        Assert.True(judge.JudgeName(name, Anime("One Piece", 21, 45, 1172), Blacklist.None).Accepted);
        Assert.False(judge.JudgeName(name, Anime("One Piece", 21, 46, 1173), Blacklist.None).Accepted);
    }

    /// <remarks>
    /// Quality is one rung, not a ceiling. A ceiling reads as generous and behaves as a downgrade, because the
    /// 720p copy is usually posted first and would be taken every time.
    /// </remarks>
    [Fact]
    public void AResolutionOffTheRungIsRefusedInBothDirections()
    {
        EffectiveSettings at1080 = Settings(quality: "1080p");

        Assert.True(Judge(at1080, Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")).Accepted);
        Assert.False(Judge(at1080, Real("1337x.html", "1337x", "Silo.S03E06.720p.x264-FENiX")).Accepted);
        Assert.False(Judge(at1080, Real("1337x.html", "1337x", "Silo.S03E06.The.Drive.2160p.ATVP.WEB-DL.ITA.ENG.DDP5.1.Atmos.DV.HDR.H.265-G66.mkv")).Accepted);
    }

    /// <remarks>
    /// A release that does not say what resolution it is cannot be shown to be on the rung, and is refused for
    /// that reason and not for being 720p — a different sentence for the owner to read.
    /// </remarks>
    [Fact]
    public void AReleaseThatNamesNoResolutionIsRefused()
    {
        Verdict verdict = Judge(Settings(quality: "1080p"), Real("eztv.html", "eztv", "Silo S03E06 XviD-AFG"));

        Assert.False(verdict.Accepted);
        Assert.Contains("resolution", verdict.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A blacklisted title is refused as a name, and a blacklisted hash as a copy: a torrent that failed to
    /// download is worth refusing under whichever name it is offered next.
    /// </remarks>
    [Fact]
    public void ABlacklistedTitleOrHashIsRefused()
    {
        ReleaseName name = ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));

        Assert.False(new NameJudge(Settings(quality: "1080p"))
            .JudgeName(name, SiloSix, Blacklist.Of(Blacklist.KeyOf("Silo.S03E06.1080p.x265-ELiTE")))
            .Accepted);

        Assert.False(NameJudge.JudgeCopy(Copy(40, "92D8A3F6864911EF292B4BE0DD5286406396D2B3"), Blacklist.Of("92D8A3F6864911EF292B4BE0DD5286406396D2B3")).Accepted);
    }

    /// <remarks>
    /// <strong>Only a video file.</strong> On 22 August 2026 the owner's server grabbed
    /// <c>Lioness 2023 S03E02 1080p WEB h264-ETHEL.exe</c> — 1.2 GB of executable named after an episode.
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
        Assert.Equal(accepted, Judge(Settings(quality: "1080p"), title).Accepted);
    }

    /// <remarks>
    /// The release group is not a file type: <c>Greek S01E01 HR HDTV XviD-2HD</c> disappeared the first time
    /// this was written by taking the last word blindly.
    /// </remarks>
    [Theory]
    [InlineData("Greek S01E01 HR HDTV XviD-2HD")]
    [InlineData("Silo.S03E06.1080p.WEB.H264-FQM")]
    [InlineData("Silo.S03E06.PROPER.1080p.WEB.H264-NTb")]
    public void AReleaseGroupIsNotAFileType(string title)
    {
        Assert.Null(TitleMatcher.FileType(title));
    }

    private static ReleaseCopy Copy(int? seeders, string? hash = null)
    {
        return new("Silo.S03E06.1080p.x265-ELiTE", "LimeTorrents", 35, hash, null, null, seeders, null);
    }

    private static TrackedEpisode Anime(string show, int season, int number, int absolute)
    {
        return new(new(77, season, number), show, null, LibraryKind.Anime, null, new DateOnly(2026, 8, 1), EpisodeState.Missing, absolute);
    }

    private static Verdict Judge(EffectiveSettings settings, string name)
    {
        return new NameJudge(settings).JudgeName(ReleaseName.Parse(name), SiloSix, Blacklist.None);
    }

    private static EffectiveSettings Settings(
        string? quality,
        string codec = LibraryPreferences.AnyCodec,
        IReadOnlyList<string>? wishes = null,
        IReadOnlyList<string>? musts = null,
        IReadOnlyList<string>? forbidden = null)
    {
        return new()
        {
            Quality = quality,
            Codec = codec,
            Wishes = wishes ?? [],
            Musts = musts ?? [],
            Forbidden = forbidden ?? [],
            Searched = quality is not null,
        };
    }

    private static TrackedEpisode Episode(string show, int season, int number)
    {
        return new(new(41, season, number), show, null, LibraryKind.Television, null, new DateOnly(2026, 8, 1), EpisodeState.Missing);
    }

    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return name;
    }
}
