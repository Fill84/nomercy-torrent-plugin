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

public class QualityLadderTests
{
    private static QualityLadder Ladder() =>
        new(
            [
                new QualityDefinition("HDTV-720p", Resolution.Hd720, ReleaseSource.Hdtv),
                new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                new QualityDefinition("BluRay-1080p", Resolution.Fhd1080, ReleaseSource.BluRay),
            ],
            "WEB-1080p"
        );

    [Fact]
    public void RankOf_OrdersByLadderPosition()
    {
        QualityLadder ladder = Ladder();

        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().Be(3);
        ladder.RankOf(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().Be(2);
        ladder.RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().Be(0);
    }

    [Fact]
    public void RankOf_ReturnsMinusOneForAQualityNotOnTheLadder()
    {
        Ladder().RankOf(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().Be(-1);
    }

    [Fact]
    public void RankOf_PrefersTheMostSpecificRung()
    {
        Ladder()
            .RankOf(new Quality(Resolution.Hd720, ReleaseSource.Hdtv))
            .Should()
            .Be(0, "the HDTV-specific rung must win over the source-agnostic WEB-720p rung");
    }

    [Fact]
    public void IsAllowed_IsTrueOnlyForQualitiesOnTheLadder()
    {
        QualityLadder ladder = Ladder();

        ladder.IsAllowed(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.IsAllowed(new Quality(Resolution.Sd480, ReleaseSource.WebRip)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsTrueAtOrAboveTheCutoffRung()
    {
        QualityLadder ladder = Ladder();

        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.WebDl)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Fhd1080, ReleaseSource.BluRay)).Should().BeTrue();
        ladder.MeetsCutoff(new Quality(Resolution.Hd720, ReleaseSource.Hdtv)).Should().BeFalse();
    }

    [Fact]
    public void MeetsCutoff_IsFalseForAQualityNotOnTheLadder()
    {
        Ladder().MeetsCutoff(new Quality(Resolution.Uhd2160, ReleaseSource.WebDl)).Should().BeFalse();
    }

    [Fact]
    public void Constructor_ThrowsWhenTheCutoffNameMatchesNoRung()
    {
        Action act = () =>
            new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-4K"
            );

        act.Should().Throw<ArgumentException>().WithMessage("*WEB-4K*");
    }

    [Fact]
    public void Constructor_AcceptsACutoffNameThatMatchesARung()
    {
        Action act = () =>
            new QualityLadder(
                [
                    new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                    new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
                ],
                "WEB-1080p"
            );

        act.Should().NotThrow();
    }

    [Fact]
    public void WithExpression_RejectsACutoffNameThatNamesNoRung()
    {
        QualityLadder ladder = Ladder();

        Action act = () => _ = ladder with { CutoffName = "NOPE-DOES-NOT-EXIST" };

        act.Should().Throw<ArgumentException>();
    }
}
