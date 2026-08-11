// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class ReleaseFilterTests
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
            MinSeeders = 3,
            MaxSizeBytes = 10L * 1024 * 1024 * 1024,
        };

    private static FilterContext Context(
        ReleaseProfile? profile = null,
        EpisodeSlot? slot = null,
        IReadOnlySet<string>? titles = null,
        IReadOnlySet<string>? hashes = null
    ) =>
        new(
            "Silo",
            slot ?? new EpisodeSlot(3, 4),
            profile ?? Profile(),
            titles ?? new HashSet<string>(),
            hashes ?? new HashSet<string>()
        );

    private static ReleaseInfo Release(
        string title,
        int seeders = 50,
        long size = 2L * 1024 * 1024 * 1024,
        string? infoHash = null
    ) =>
        new()
        {
            IndexerName = "test",
            TorrentId = "1",
            Title = title,
            Seeders = seeders,
            SizeBytes = size,
            InfoHash = infoHash,
        };

    private static FilterVerdict Evaluate(ReleaseInfo release, FilterContext context) =>
        new ReleaseFilter().Evaluate(release, ReleaseNameParser.Parse(release.Title), context);

    [Fact]
    public void Evaluate_AcceptsAReleaseThatPassesEveryRule()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOfADifferentShow()
    {
        FilterVerdict verdict = Evaluate(
            Release("Lucky.Hank.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("show name");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOfADifferentEpisode()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E05.1080p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be("release is S03E05, not the wanted S03E04");
    }

    [Fact]
    public void Evaluate_RejectsASeasonPackWhenPacksAreNotAllowed()
    {
        FilterVerdict verdict = Evaluate(Release("Silo.S03.1080p.WEB-DL.H264-CAKES"), Context());

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be("season pack not allowed by profile");
    }

    [Fact]
    public void Evaluate_AcceptsASeasonPackOfTheWantedSeasonWhenPacksAreAllowed()
    {
        ReleaseProfile profile = Profile() with { AllowSeasonPacks = true };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAReleaseMissingARequiredLanguage()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["Japanese"], [], [], false),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("Japanese");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseCarryingAForbiddenLanguage()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["English"], [], ["German"], false),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.GERMAN.ENG.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("German");
    }

    [Fact]
    public void Evaluate_RejectsANonDualAudioReleaseWhenDualAudioIsRequired()
    {
        ReleaseProfile profile = Profile() with
        {
            Language = new LanguageProfile(["English"], [], [], true),
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("dual audio");
    }

    [Fact]
    public void Evaluate_RejectsABlockedReleaseGroup()
    {
        ReleaseProfile profile = Profile() with { BlockedGroups = ["CAKES"] };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("CAKES");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseMissingARequiredTerm()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("AMZN", TermKind.Required, 0)],
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("AMZN");
    }

    [Fact]
    public void Evaluate_TreatsAnInvalidForbiddenTermPatternAsNotPresent()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("*HDR*", TermKind.Forbidden, 0)],
        };

        Action act = () =>
            Evaluate(Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"), Context(profile));

        act.Should().NotThrow();
        Evaluate(Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"), Context(profile))
            .Accepted.Should()
            .BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsWithTheBadPatternWhenARequiredTermPatternIsInvalid()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("*HDR*", TermKind.Required, 0)],
        };

        Action act = () =>
            Evaluate(Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"), Context(profile));
        act.Should().NotThrow();

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be("required term missing: *HDR*");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseCarryingAForbiddenTerm()
    {
        ReleaseProfile profile = Profile() with
        {
            Terms = [new TermRule("HDR", TermKind.Forbidden, 0)],
        };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.HDR.WEB-DL.H264-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("HDR");
    }

    [Fact]
    public void Evaluate_RejectsAQualityThatIsNotOnTheLadder()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.2160p.WEB-DL.H264-CAKES"),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("quality");
    }

    [Fact]
    public void Evaluate_RejectsTheWrongCodecWhenTheProfileNamesOne()
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264 };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.x265-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("codec");
    }

    [Fact]
    public void Evaluate_AcceptsAnUntaggedCodecWhenTheProfileDoesNotRequireATag()
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264, RequireCodecTag = false };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAnUntaggedCodecWhenTheProfileRequiresATag()
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264, RequireCodecTag = true };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("untagged");
        verdict.Reason.Should().Contain("requires");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Evaluate_RejectsAnExplicitlyWrongCodecRegardlessOfRequireCodecTag(bool requireCodecTag)
    {
        ReleaseProfile profile = Profile() with { Codec = VideoCodec.H264, RequireCodecTag = requireCodecTag };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.x265-CAKES"),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("codec");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseOverTheSizeLimit()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", size: 40L * 1024 * 1024 * 1024),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be("size 40.0 GB over limit 10.0 GB");
    }

    [Fact]
    public void Evaluate_RejectsAReleaseUnderTheSizeFloor()
    {
        ReleaseProfile profile = Profile() with { MinSizeBytes = 1L * 1024 * 1024 * 1024 };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", size: 500L * 1024 * 1024),
            Context(profile)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Be("size 0.5 GB under floor 1.0 GB");
    }

    [Fact]
    public void Evaluate_AllowsAnUnknownSizeThroughTheFloorCheck()
    {
        ReleaseProfile profile = Profile() with { MinSizeBytes = 1L * 1024 * 1024 * 1024 };

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", size: 0),
            Context(profile)
        );

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RejectsAReleaseBelowTheSeederFloor()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", seeders: 1),
            Context()
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("seeders");
    }

    [Fact]
    public void Evaluate_RejectsABlacklistedTitle()
    {
        HashSet<string> titles = [TitleMatcher.Normalize("Silo.S03E04.1080p.WEB-DL.H264-CAKES")];

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES"),
            Context(titles: titles)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("release title is blacklisted");
    }

    [Fact]
    public void Evaluate_RejectsABlacklistedInfoHashRegardlessOfCase()
    {
        HashSet<string> hashes = ["abc123"];

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", infoHash: "ABC123"),
            Context(hashes: hashes)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("info hash ABC123 is blacklisted");
    }

    [Fact]
    public void Evaluate_RejectsAnUppercaseStoredHashAgainstALowercaseCandidate()
    {
        HashSet<string> hashes = ["ABC123"];

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E04.1080p.WEB-DL.H264-CAKES", infoHash: "abc123"),
            Context(hashes: hashes)
        );

        verdict.Accepted.Should().BeFalse();
        verdict.Reason.Should().Contain("info hash abc123 is blacklisted");
    }

    [Fact]
    public void Evaluate_SkipsTheEpisodeCheckWhenNoSlotIsWanted()
    {
        FilterContext context = new(
            "Silo",
            null,
            Profile(),
            new HashSet<string>(),
            new HashSet<string>()
        );

        FilterVerdict verdict = Evaluate(
            Release("Silo.S03E09.1080p.WEB-DL.H264-CAKES"),
            context
        );

        verdict.Accepted.Should().BeTrue();
    }

    /// <summary>
    /// Every one of these was grabbed on the owner's server against an English-only
    /// profile. MULTI names no language, so the extractor found none and defaulted to
    /// English; ITA.ENG names English among others, and "at least one of the required" is
    /// satisfied by that. Both readings were wrong in the same way: "only" was being
    /// checked as "among".
    /// </summary>
    [Theory]
    [InlineData("Silo S03E04 MULTI 1080p WEB H264-HiggsBoson")]
    [InlineData("Silo.S03E04.MULTi3.1080p.WEB.H264-GROUP")]
    [InlineData("Silo.S03E04.ITA.ENG.1080p.WEB.H264-GROUP")]
    [InlineData("Silo.S03E04.FR.ENG.1080p.WEB.H264-GROUP")]
    [InlineData("Silo.S03E04.DUBBED.1080p.WEB.H264-GROUP")]
    [InlineData("Silo.S03E04.Cap.304.1080p.WEB.H264-GROUP")]
    public void Evaluate_RefusesASecondLanguageEvenWhenEnglishIsOneOfThem(string title)
    {
        FilterVerdict verdict = Evaluate(
            Release(title),
            Context(Profile() with { Language = LanguageProfile.EnglishOnly }));

        verdict.Accepted.Should().BeFalse();
    }

    [Theory]
    [InlineData("Silo.S03E04.1080p.WEB.H264-CAKES")]
    [InlineData("Silo.S03E04.ENG.1080p.WEB.H264-CAKES")]
    public void Evaluate_LeavesAnEnglishReleaseAlone(string title)
    {
        FilterVerdict verdict = Evaluate(
            Release(title),
            Context(Profile() with { Language = LanguageProfile.EnglishOnly }));

        verdict.Accepted.Should().BeTrue();
    }

    /// <summary>A library that is not English asks nothing, and the MULTI copy is fine.</summary>
    [Fact]
    public void Evaluate_AsksNothingAboutLanguageWhenTheOwnerTurnedTheRuleOff()
    {
        FilterVerdict verdict = Evaluate(
            Release("Silo S03E04 MULTI 1080p WEB H264-HiggsBoson"),
            Context(Profile() with { Language = LanguageProfile.Any }));

        verdict.Accepted.Should().BeTrue();
    }

    [Fact]
    public void Evaluate_RefusesATermTheOwnerExcluded()
    {
        ReleaseProfile excluding = Profile() with
        {
            Terms = [new TermRule("HiggsBoson", TermKind.Forbidden, 0)],
        };

        Evaluate(Release("Silo.S03E04.1080p.WEB.H264-HiggsBoson"), Context(excluding))
            .Accepted.Should().BeFalse();

        Evaluate(Release("Silo.S03E04.1080p.WEB.H264-CAKES"), Context(excluding))
            .Accepted.Should().BeTrue();
    }
}
