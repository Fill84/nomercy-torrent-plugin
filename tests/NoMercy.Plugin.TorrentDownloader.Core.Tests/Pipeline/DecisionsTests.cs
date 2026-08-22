using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// What one cycle has decided so far.
/// </summary>
/// <remarks>
/// The rules that need to know about more than one episode at a time: whether a
/// season has enough gaps to be worth a pack, which episodes a pack already
/// taken has settled, and what was refused and why. <strong>H1:</strong> the
/// real profile throughout, never a stand-in chooser.
/// </remarks>
public class DecisionsTests
{
    /// <remarks>
    /// A pack is worth its bytes when the season has enough gaps in it. Two
    /// missing episodes out of ten do not justify downloading the season, and
    /// the threshold is the owner's to set.
    /// </remarks>
    [Fact]
    public void APackIsRefusedUntilTheSeasonHasEnoughGaps()
    {
        Profile profile = new() { MaximumResolution = "1080p", EnglishOnly = false, SeasonPackThreshold = 3 };

        Decisions two = new(profile, [Gap(1), Gap(2)], Blacklist.None);
        Decisions three = new(profile, [Gap(1), Gap(2), Gap(3)], Blacklist.None);

        Verdict refused = two.JudgeName(Pack, Gap(1));

        Assert.False(refused.Accepted);
        Assert.Contains("gaps", refused.Reason, StringComparison.OrdinalIgnoreCase);

        Assert.True(three.JudgeName(Pack, Gap(1)).Accepted);
    }

    /// <remarks>
    /// And only when the owner wants packs at all. The threshold does not
    /// override the switch: a season with twenty gaps in it is still twenty
    /// episodes to somebody who does not want packs.
    /// </remarks>
    [Fact]
    public void APackIsRefusedWhenPacksAreNotWantedHoweverManyGapsThereAre()
    {
        Decisions decisions = new(
            new() { MaximumResolution = "1080p", EnglishOnly = false, AllowSeasonPacks = false },
            [Gap(1), Gap(2), Gap(3), Gap(4)],
            Blacklist.None);

        Assert.False(decisions.JudgeName(Pack, Gap(1)).Accepted);
    }

    /// <remarks>
    /// A pack that is taken answers for every gap in the season it covers, and
    /// those episodes are not asked about again this cycle. Asking again is a
    /// search per episode for a file already on its way, and a second grab of
    /// the same season.
    /// </remarks>
    [Fact]
    public void APackThatIsTakenSettlesEveryGapInItsSeason()
    {
        Decisions decisions = new(
            new() { MaximumResolution = "1080p", EnglishOnly = false, MinimumSeeders = 2 },
            [Gap(1), Gap(2), Gap(3)],
            Blacklist.None);

        Assert.False(decisions.Settled(Gap(2).Key));

        Decision decision = decisions.Rank(Gap(1), [Copy(Pack.Original, seeders: 40)]);

        Assert.NotNull(decision.Chosen);

        // Settled when it is taken, not when it is chosen: a copy that turns
        // out to have no route to a torrent settles nothing.
        decisions.Settle(Gap(1), decision.Chosen!);

        Assert.True(decisions.Settled(Gap(1).Key));
        Assert.True(decisions.Settled(Gap(2).Key));
        Assert.True(decisions.Settled(Gap(3).Key));
    }

    /// <remarks>
    /// A single episode settles itself and nothing else. Reading it as a pack
    /// would leave the rest of the season unsearched for the cycle.
    /// </remarks>
    [Fact]
    public void ASingleEpisodeSettlesOnlyItself()
    {
        Decisions decisions = new(
            new() { MaximumResolution = "720p", MinimumSeeders = 2 },
            [Silo(6), Silo(7)],
            Blacklist.None);

        Decision decision = decisions.Rank(Silo(6), [Copy(Single.Original, seeders: 40)]);

        decisions.Settle(Silo(6), decision.Chosen!);

        Assert.True(decisions.Settled(Silo(6).Key));
        Assert.False(decisions.Settled(Silo(7).Key));
    }

    /// <remarks>
    /// A blacklisted title is never chosen, and neither is a blacklisted hash.
    /// A torrent that failed to download is worth refusing under whichever name
    /// it is offered next.
    /// </remarks>
    [Fact]
    public void ABlacklistedTitleOrHashIsNeverChosen()
    {
        Decisions byTitle = new(
            new() { MaximumResolution = "720p", MinimumSeeders = 2 },
            [Silo(6)],
            Blacklist.Of(Blacklist.KeyOf(Single.Original)));

        Assert.False(byTitle.JudgeName(Single, Silo(6)).Accepted);
        Assert.Null(byTitle.Rank(Silo(6), [Copy(Single.Original, seeders: 40)]).Chosen);

        Decisions byHash = new(
            new() { MaximumResolution = "720p", MinimumSeeders = 2 },
            [Silo(6)],
            Blacklist.Of(Hash));

        Assert.Null(byHash.Rank(Silo(6), [Copy(Single.Original, seeders: 40)]).Chosen);
    }

    /// <remarks>
    /// Every refusal is kept with the episode it was refused for and the reason
    /// it was refused, which is what the Skipped page renders and what the
    /// control to allow one anyway acts on. "Nothing worth taking" is the
    /// sentence that hid a release's worth of faults.
    /// </remarks>
    [Fact]
    public void ARefusedReleaseIsRecordedWithItsReason()
    {
        Decisions decisions = new(
            new() { MaximumResolution = "720p", MinimumSeeders = 10 },
            [Silo(6)],
            Blacklist.None);

        decisions.Rank(Silo(6), [Copy(Single.Original, seeders: 1)]);

        SkippedRelease skipped = Assert.Single(decisions.Skipped);

        Assert.Equal(Silo(6).Key, skipped.Episode);
        Assert.Equal(Single.Original, skipped.Title);
        Assert.Equal("LimeTorrents", skipped.Source);
        Assert.Contains("10 are wanted", skipped.Reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A name the profile refuses is recorded too, and with no site against it:
    /// nothing was asked, so no site refused anything. The Skipped page reads
    /// very differently from one that lists a site beside every line.
    /// </remarks>
    [Fact]
    public void ANameTheProfileRefusesIsRecordedWithNoSiteAgainstIt()
    {
        Decisions decisions = new(
            new() { MaximumResolution = "2160p" },
            [Silo(6)],
            Blacklist.None);

        Verdict verdict = decisions.JudgeName(Single, Silo(6));

        Assert.False(verdict.Accepted);

        SkippedRelease skipped = Assert.Single(decisions.Skipped);

        Assert.Null(skipped.Source);
        Assert.Contains("720p is not 2160p", skipped.Reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The gaps counted are the ones this cycle is looking at, per show and per
    /// season. Another show's gaps are another show's.
    /// </remarks>
    [Fact]
    public void GapsAreCountedPerShowAndPerSeason()
    {
        Decisions decisions = new(
            new(),
            [Gap(1), Gap(2), OtherSeason(1), OtherShow(1), OtherShow(2)],
            Blacklist.None);

        Assert.Equal(2, decisions.GapsIn(Show, 5));
        Assert.Equal(1, decisions.GapsIn(Show, 6));
        Assert.Equal(2, decisions.GapsIn(Show + 1, 5));
    }

    private const string Title = "Pokemon Master Quest";

    private static readonly int Show = Title.GetHashCode(StringComparison.Ordinal);

    private const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

    /// <summary>A real season pack, off the Nyaa capture.</summary>
    private static readonly ReleaseName Pack = ReleaseName.Parse(Real(
        "nyaa-diacritic.xml",
        "torrent-rss",
        "[T3KASHi] Pokemon Master Quest S05 TRUEFRENCH 1080p WEB-DL H.264 (VF)"));

    /// <summary>And a real single episode, off the PreDB capture.</summary>
    private static readonly ReleaseName Single = ReleaseName.Parse(Real(
        "predb.xml",
        "rss",
        "Silo.S03E06.720p.WEB.H264-SYLiX"));

    private static ReleaseCopy Copy(string title, int seeders)
    {
        return new(title, "LimeTorrents", 35, Hash, $"magnet:?xt=urn:btih:{Hash}", null, seeders);
    }

    /// <summary>An episode of the show the single release above is for.</summary>
    private static TrackedEpisode Silo(int number)
    {
        return new(
            new("Silo".GetHashCode(StringComparison.Ordinal), 3, number),
            "Silo",
            null,
            LibraryKind.Television,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
    }

    private static TrackedEpisode Gap(int number)
    {
        return Episode(Show, 5, number);
    }

    private static TrackedEpisode OtherSeason(int number)
    {
        return Episode(Show, 6, number);
    }

    private static TrackedEpisode OtherShow(int number)
    {
        return Episode(Show + 1, 5, number);
    }

    private static TrackedEpisode Episode(int show, int season, int number)
    {
        return new(
            new(show, season, number),
            Title,
            null,
            LibraryKind.Anime,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing);
    }

    private static string Real(string fixture, string reader, string name)
    {
        Assert.Contains(name, Capture.Rows(fixture, reader));

        return name;
    }
}
