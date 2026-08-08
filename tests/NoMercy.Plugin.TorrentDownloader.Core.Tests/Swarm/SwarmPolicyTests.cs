// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

public class SwarmPolicyTests
{
    [Fact]
    public void Default_CarriesTheValuesTheDesignSettled_On()
    {
        SwarmPolicy policy = SwarmPolicy.Default;

        policy.MaxConnectionsPerTorrent.Should().Be(100);
        policy.MaxHalfOpenConnections.Should().Be(20);
        policy.NoPeersTimeout.Should().Be(TimeSpan.FromMinutes(30));
        policy.MetadataTimeout.Should().Be(TimeSpan.FromMinutes(5));
        policy.MaxPieceFailuresPerPeer.Should().Be(3);
        policy.EndgameThreshold.Should().Be(0.05);
        policy.SeedRatioTarget.Should().Be(1.0);
        policy.SeedTimeTarget.Should().Be(TimeSpan.FromHours(72));
    }

    [Fact]
    public void MayUpload_IsFalseForAPublicTorrentWhateverElseIsSet()
    {
        // The requirement is not "usually off". A public torrent has no path to
        // uploading at all, so no combination of settings can open one.
        SwarmPolicy eager = SwarmPolicy.Default with { SeedRatioTarget = 99, SeedTimeTarget = TimeSpan.MaxValue };

        eager.MayUpload(TorrentOrigin.Public).Should().BeFalse();
        SwarmPolicy.Default.MayUpload(TorrentOrigin.Public).Should().BeFalse();
    }

    [Fact]
    public void MayUpload_IsFalseForAPrivateTorrentUntilSeedingIsTurnedOn()
    {
        SwarmPolicy.Default.MayUpload(TorrentOrigin.PrivateWithoutSeeding).Should().BeFalse();
    }

    [Fact]
    public void MayUpload_IsTrueOnlyForAPrivateTorrentConfiguredToSeed()
    {
        SwarmPolicy.Default.MayUpload(TorrentOrigin.PrivateSeeding).Should().BeTrue();
    }

    [Fact]
    public void HasRoomForAnotherPeer_StopsAtTheCeiling()
    {
        SwarmPolicy policy = SwarmPolicy.Default with { MaxConnectionsPerTorrent = 3 };

        policy.HasRoomForAnotherPeer(2).Should().BeTrue();
        policy.HasRoomForAnotherPeer(3).Should().BeFalse();
        policy.HasRoomForAnotherPeer(4).Should().BeFalse();
    }

    [Fact]
    public void MayDialAnother_StopsAtTheHalfOpenCeiling()
    {
        SwarmPolicy policy = SwarmPolicy.Default with { MaxHalfOpenConnections = 2 };

        policy.MayDialAnother(halfOpen: 1).Should().BeTrue();
        policy.MayDialAnother(halfOpen: 2).Should().BeFalse();
    }

    [Theory]
    [InlineData(100, 6, false)]
    [InlineData(100, 5, true)]
    [InlineData(100, 1, true)]
    [InlineData(100, 0, false)]
    public void ShouldEnterEndgame_TurnsOnInsideTheLastFewPercent(int total, int remaining, bool expected)
    {
        // Nothing outstanding is not endgame, it is done.
        SwarmPolicy.Default.ShouldEnterEndgame(remaining, total).Should().Be(expected);
    }

    [Fact]
    public void ShouldEnterEndgame_AlwaysTurnsOnForATinyTorrent()
    {
        // Five percent of four pieces rounds to nothing, and a two-piece torrent that
        // never enters endgame can still park forever on one slow peer.
        SwarmPolicy.Default.ShouldEnterEndgame(remaining: 1, total: 4).Should().BeTrue();
    }

    [Theory]
    [InlineData(2, false)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    public void ShouldBan_WaitsForAPatternRatherThanOneFailure(int failures, bool expected)
    {
        SwarmPolicy.Default.ShouldBan(failures).Should().Be(expected);
    }

    [Fact]
    public void ShouldStopSeeding_StopsAtWhicheverTargetLandsFirst()
    {
        SwarmPolicy policy = SwarmPolicy.Default;

        policy.ShouldStopSeeding(ratio: 0.5, elapsed: TimeSpan.FromHours(1)).Should().BeFalse();
        policy.ShouldStopSeeding(ratio: 1.0, elapsed: TimeSpan.FromHours(1)).Should().BeTrue();
        policy.ShouldStopSeeding(ratio: 0.1, elapsed: TimeSpan.FromHours(72)).Should().BeTrue();
    }
}
