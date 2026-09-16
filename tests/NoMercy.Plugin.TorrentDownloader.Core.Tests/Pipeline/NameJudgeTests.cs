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

    /// <remarks>
    /// <para>
    /// <strong>English only, back as a setting on the owner's word of 16 September 2026.</strong> It went
    /// with the global profile and there was nothing in its place, so a German release was refused only
    /// where the owner had thought to forbid the word themselves.
    /// </para>
    /// <para>
    /// A name claiming any language that is not English is refused, and the reason names the claim. A name
    /// claiming none is taken: most English releases say nothing about language at all, so refusing the
    /// untagged ones would refuse nearly everything. <c>MULTi</c> and a dual audio are refused beside an
    /// English tag — that is the release carrying English <em>and</em> others, which is what took
    /// <c>Silo.S03E07.MULTI.1080p.WEB.H264-HiggsBoson</c> for an owner who wanted the plain one.
    /// </para>
    /// </remarks>
    [Fact]
    public void WithEnglishOnlyOnAReleaseMarkedAnotherLanguageIsRefused()
    {
        EffectiveSettings english = Settings(quality: "1080p", englishOnly: true);

        // A real row, and the shape the fault took: English audio and Italian
        // beside it, which is not the plain English release.
        const string Italian = "Silo.S03E06.The.Drive.1080p.ATVP.WEB-DL.DDP5.1.ENG.ITA.Atmos.H265-TheBlackKing.mkv";

        Verdict italian = Judge(english, Real("torrentz2.html", "torrentz2", Italian));

        Assert.False(italian.Accepted);
        Assert.Contains("italian", italian.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("English only", italian.Reason, StringComparison.Ordinal);

        // The same name with the setting off is taken, so the refusal is the
        // setting's and not the name's.
        Assert.True(Judge(Settings(quality: "1080p", codec: "h265"), Real("torrentz2.html", "torrentz2", Italian)).Accepted);

        // Several languages at once, English among them, is still not the plain
        // English release.
        Assert.False(Judge(english, Real("torrentz2.html", "torrentz2", "Silo.S03E06.1080p.WEB-DL.DUAL.5.1")).Accepted);

        // And a name that claims no language is taken, which is most of them.
        Assert.True(Judge(english, Real("eztv.html", "eztv", "Silo S03E06 1080p WEB H264-CAKES")).Accepted);
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
    /// indexer was ever asked. No setting carries a seeder threshold, and since 15 September 2026 no torrent
    /// is judged on its seeders either: the winner is the one the most indexers list.
    /// </remarks>
    [Fact]
    public void ANameIsNeverJudgedOnSeeders()
    {
        Assert.True(Judge(Settings(quality: "1080p"), Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE")).Accepted);
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
    /// A release name names one episode by its season and episode number (<c>docs/specs/release-names.md</c>).
    /// An absolute-numbered post names none, even on the episode whose absolute number it carries, and the
    /// owner reads why: it is this show's name, and not this episode.
    /// </remarks>
    [Fact]
    public void AnAbsoluteNumberedNameIsRefusedEvenForItsOwnEpisode()
    {
        ReleaseName name = ReleaseName.Parse(Real("nyaa-absolute.xml", "torrent-rss", "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv"));

        Verdict verdict = new NameJudge(Settings(quality: "1080p", codec: "h265"))
            .JudgeName(name, Anime("One Piece", 21, 45, 1172), Blacklist.None);

        Assert.False(verdict.Accepted);
        Assert.Contains("is not", verdict.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("not a release of", verdict.Reason, StringComparison.Ordinal);
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
    /// A blacklisted title is refused as a name, before any indexer is asked for it. A blacklisted hash is the
    /// winner's to drop (<c>WinnerTests</c>).
    /// </remarks>
    [Fact]
    public void ABlacklistedTitleIsRefused()
    {
        ReleaseName name = ReleaseName.Parse(Real("1337x.html", "1337x", "Silo.S03E06.1080p.x265-ELiTE"));

        Assert.False(new NameJudge(Settings(quality: "1080p"))
            .JudgeName(name, SiloSix, Blacklist.Of(Blacklist.KeyOf("Silo.S03E06.1080p.x265-ELiTE")))
            .Accepted);
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
        IReadOnlyList<string>? forbidden = null,
        bool englishOnly = false)
    {
        return new()
        {
            Quality = quality,
            Codec = codec,
            EnglishOnly = englishOnly,
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
