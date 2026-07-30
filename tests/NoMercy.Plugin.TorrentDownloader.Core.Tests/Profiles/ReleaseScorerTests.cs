// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class ReleaseScorerTests
{
    private static ReleaseProfile Profile() =>
        new()
        {
            Name = "default",
            Quality = new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-1080p"
            ),
        };

    private static ReleaseInfo Release(string title, int seeders = 10, int indexerPriority = 0) =>
        new()
        {
            IndexerName = "test",
            TorrentId = "1",
            Title = title,
            Seeders = seeders,
            IndexerPriority = indexerPriority,
        };

    private static int Score(ReleaseInfo release, ScoreContext context) =>
        new ReleaseScorer().Score(release, ReleaseNameParser.Parse(release.Title), context);

    [Fact]
    public void Score_RanksAQualityStepAboveAnySeederDifference()
    {
        ScoreContext context = new(Profile(), null);

        int betterQuality = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 4), context);
        int moreSeeders = Score(Release("Silo.S03E04.720p.WEB.H264-B", seeders: 5000), context);

        betterQuality.Should().BeGreaterThan(moreSeeders);
    }

    [Fact]
    public void Score_UsesSeedersOnlyAsATieBreak()
    {
        ScoreContext context = new(Profile(), null);

        int many = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 5000), context);
        int few = Score(Release("Silo.S03E04.1080p.WEB.H264-A", seeders: 4), context);

        many.Should().BeGreaterThan(few);
        (many - few).Should().BeLessThan(10_000);
    }

    [Fact]
    public void Score_BoostsTheExactAnnouncedSceneRelease()
    {
        ScoreContext context = new(Profile(), "Silo.S03E04.1080p.WEB.H264-CAKES");

        int announced = Score(Release("Silo.S03E04.1080p.WEB.H264-CAKES"), context);
        int other = Score(Release("Silo.S03E04.1080p.WEB.H264-OTHER"), context);

        announced.Should().BeGreaterThan(other);
    }

    [Fact]
    public void Score_AppliesPreferredGroupWeightInBothDirections()
    {
        ReleaseProfile profile = Profile() with
        {
            PreferredGroups = [new GroupPreference("CAKES", 10), new GroupPreference("BAD", -10)],
        };
        ScoreContext context = new(profile, null);

        int preferred = Score(Release("Silo.S03E04.1080p.WEB.H264-CAKES"), context);
        int neutral = Score(Release("Silo.S03E04.1080p.WEB.H264-NEUTRAL"), context);
        int discouraged = Score(Release("Silo.S03E04.1080p.WEB.H264-BAD"), context);

        preferred.Should().BeGreaterThan(neutral);
        discouraged.Should().BeLessThan(neutral);
    }

    [Fact]
    public void Score_RewardsPreferredTerms()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("AMZN", TermKind.Preferred, 5)],
        };
        ScoreContext context = new(profile, null);

        int withTerm = Score(Release("Silo.S03E04.1080p.AMZN.WEB.H264-A"), context);
        int withoutTerm = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);

        withTerm.Should().BeGreaterThan(withoutTerm);
    }

    [Fact]
    public void Score_AwardsNoBonusForAnInvalidPreferredTermPattern()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("*HDR*", TermKind.Preferred, 5)],
        };
        ScoreContext context = new(profile, null);

        Action act = () => Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);
        act.Should().NotThrow();

        int withInvalidTerm = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);
        int withoutTerm = Score(
            Release("Silo.S03E04.1080p.WEB.H264-A"),
            new ScoreContext(Profile(), null)
        );

        withInvalidTerm.Should().Be(withoutTerm);
    }

    [Fact]
    public void Score_RewardsProperAndRepack()
    {
        ScoreContext context = new(Profile(), null);

        int proper = Score(Release("Silo.S03E04.PROPER.1080p.WEB.H264-A"), context);
        int plain = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);

        proper.Should().BeGreaterThan(plain);
    }

    [Fact]
    public void Score_RewardsDualAudioOnlyWhenTheProfileWantsIt()
    {
        ReleaseProfile wanting = Profile() with
        {
            Language = new LanguageProfile(["English"], ["Japanese"], [], true),
        };

        int scoredWhenWanted = Score(
            Release("Frieren.S01E01.1080p.WEB.Dual.Audio.H264-A"),
            new ScoreContext(wanting, null)
        );
        int scoredWhenIndifferent = Score(
            Release("Frieren.S01E01.1080p.WEB.Dual.Audio.H264-A"),
            new ScoreContext(Profile(), null)
        );

        scoredWhenWanted.Should().BeGreaterThan(scoredWhenIndifferent);
    }

    [Fact]
    public void Score_RewardsAReleaseCarryingAPreferredLanguage()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["English"], ["French"], [], false),
        };
        ScoreContext context = new(profile, null);

        int withPreferred = Score(Release("Silo.S03E04.FRENCH.1080p.WEB.H264-A"), context);
        int withoutPreferred = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);

        withPreferred.Should().BeGreaterThan(withoutPreferred);
    }

    [Fact]
    public void Score_RewardsAMatchingCodecWhenTheProfileNamesOne()
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264 };
        ScoreContext context = new(profile, null);

        int matching = Score(Release("Silo.S03E04.1080p.WEB.H264-A"), context);
        int nonMatching = Score(Release("Silo.S03E04.1080p.WEB.x265-A"), context);

        matching.Should().BeGreaterThan(nonMatching);
    }

    [Fact]
    public void Score_RewardsHigherIndexerPriority()
    {
        ScoreContext context = new(Profile(), null);

        int trusted = Score(Release("Silo.S03E04.1080p.WEB.H264-A", indexerPriority: 10), context);
        int ordinary = Score(Release("Silo.S03E04.1080p.WEB.H264-A", indexerPriority: 0), context);

        trusted.Should().BeGreaterThan(ordinary);
    }

    [Fact]
    public void Score_NeverLetsPreferencesOutrankAQualityStep()
    {
        ReleaseProfile profile = Profile() with
        {
            PreferredGroups = [new GroupPreference("HUGE", 500)],
        };
        ScoreContext context = new(profile, null);

        int lowerQualityPreferredGroup = Score(Release("Silo.S03E04.720p.WEB.H264-HUGE"), context);
        int higherQualityOtherGroup = Score(Release("Silo.S03E04.1080p.WEB.H264-OTHER"), context);

        higherQualityOtherGroup.Should().BeGreaterThan(lowerQualityPreferredGroup);
    }

    [Fact]
    public void Score_TreatsAnOffLadderQualityAsTheBottomRung()
    {
        ScoreContext context = new(Profile(), null);

        int score = Score(Release("Silo.S03E04.2160p.WEB.H264-A"), context);

        score.Should().BeLessThan(10_000);
    }

    [Fact]
    public void Score_DoesNotOverflowWhenAGroupScoreIsHugeEnoughToWrapAnInt()
    {
        ReleaseProfile profile = Profile() with
        {
            PreferredGroups = [new GroupPreference("HUGE", 30_000_000)],
        };
        ScoreContext context = new(profile, null);

        int preferred = Score(Release("Silo.S03E04.1080p.WEB.H264-HUGE"), context);
        int neutral = Score(Release("Silo.S03E04.1080p.WEB.H264-NEUTRAL"), context);

        preferred.Should().BeGreaterThan(neutral);
    }
}
