// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Profiles;

public class DecisionTests
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
            MinSeeders = 2,
        };

    private static ReleaseInfo Release(string title, int seeders) =>
        new()
        {
            IndexerName = "test",
            TorrentId = title,
            Title = title,
            Seeders = seeders,
        };

    private static FilterContext Filter() =>
        new(
            "Silo",
            new EpisodeSlot(3, 4),
            Profile(),
            new HashSet<string>(),
            new HashSet<string>()
        );

    private static readonly ReleaseInfo[] Candidates =
    [
        Release("Silo.S03E04.720p.WEB.H264-HUGE", 9000),
        Release("Silo.S03E04.1080p.WEB.H264-CAKES", 12),
        Release("Silo.S03E05.1080p.WEB.H264-CAKES", 400),
        Release("Lucky.Hank.S03E04.1080p.WEB.H264-CAKES", 800),
        Release("Silo.S03E04.1080p.WEB.H264-LOWSEED", 1),
    ];

    [Fact]
    public void PickBest_ChoosesQualityOverSeeders()
    {
        CandidateVerdict? winner = new ReleaseDecider().PickBest(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        winner.Should().NotBeNull();
        winner!.Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
    }

    [Fact]
    public void Evaluate_KeepsRejectedCandidatesWithTheirReasons()
    {
        IReadOnlyList<CandidateVerdict> verdicts = new ReleaseDecider().Evaluate(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        verdicts.Should().HaveCount(5);

        CandidateVerdict wrongShow = verdicts.Single(v =>
            v.Release.Title.StartsWith("Lucky.Hank", StringComparison.Ordinal)
        );
        wrongShow.Verdict.Accepted.Should().BeFalse();
        wrongShow.Verdict.Reason.Should().Contain("show name");

        CandidateVerdict wrongEpisode = verdicts.Single(v =>
            v.Release.Title.Contains("S03E05", StringComparison.Ordinal)
        );
        wrongEpisode.Verdict.Accepted.Should().BeFalse();
        wrongEpisode.Verdict.Reason.Should().Be("release is S03E05, not the wanted S03E04");

        CandidateVerdict lowSeed = verdicts.Single(v =>
            v.Release.Title.Contains("LOWSEED", StringComparison.Ordinal)
        );
        lowSeed.Verdict.Accepted.Should().BeFalse();
        lowSeed.Verdict.Reason.Should().Contain("seeders");
    }

    [Fact]
    public void Evaluate_OrdersAcceptedCandidatesFirstThenByDescendingScore()
    {
        IReadOnlyList<CandidateVerdict> verdicts = new ReleaseDecider().Evaluate(
            Candidates,
            Filter(),
            new ScoreContext(Profile(), null)
        );

        verdicts[0].Verdict.Accepted.Should().BeTrue();
        verdicts[0].Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
        verdicts[1].Verdict.Accepted.Should().BeTrue();
        verdicts[1].Release.Title.Should().Be("Silo.S03E04.720p.WEB.H264-HUGE");
        verdicts.Skip(2).Should().OnlyContain(v => !v.Verdict.Accepted);
    }

    [Fact]
    public void PickBest_ReturnsNullWhenNothingPasses()
    {
        ReleaseInfo[] hopeless =
        [
            Release("Lucky.Hank.S03E04.1080p.WEB.H264-CAKES", 800),
            Release("Silo.S03E05.1080p.WEB.H264-CAKES", 400),
        ];

        new ReleaseDecider()
            .PickBest(hopeless, Filter(), new ScoreContext(Profile(), null))
            .Should()
            .BeNull();
    }

    [Fact]
    public void PickBest_PrefersTheAnnouncedSceneReleaseAmongEqualQualities()
    {
        ReleaseInfo[] equals =
        [
            Release("Silo.S03E04.1080p.WEB.H264-OTHER", 5000),
            Release("Silo.S03E04.1080p.WEB.H264-CAKES", 3),
        ];

        CandidateVerdict? winner = new ReleaseDecider().PickBest(
            equals,
            Filter(),
            new ScoreContext(Profile(), "Silo.S03E04.1080p.WEB.H264-CAKES")
        );

        winner!.Release.Title.Should().Be("Silo.S03E04.1080p.WEB.H264-CAKES");
    }

    [Fact]
    public void Evaluate_ReturnsAnEmptyListForNoCandidates()
    {
        new ReleaseDecider()
            .Evaluate([], Filter(), new ScoreContext(Profile(), null))
            .Should()
            .BeEmpty();
    }
}
