// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

public class PrivateTrackerRegistryTests
{
    private static PrivateTracker Seeding() => new()
    {
        Name = "Some Private Tracker",
        AnnounceUrl = "https://tracker.private.test/announce?passkey=abc123",
        Seed = true,
        SeedRatioTarget = 2.0,
        SeedTimeTarget = TimeSpan.FromHours(48),
    };

    private static PrivateTracker Leeching() => new()
    {
        Name = "Another Private Tracker",
        AnnounceUrl = "https://tracker.other.test/announce?passkey=zzz",
        Seed = false,
    };

    [Fact]
    public void OriginFor_IsPublicWhenNoConfiguredTrackerMatches()
    {
        PrivateTrackerRegistry registry = new([Seeding()]);

        registry.OriginFor(["udp://open.tracker.test:1337/announce"]).Should().Be(TorrentOrigin.Public);
    }

    [Fact]
    public void OriginFor_IsPublicWhenTheTorrentNamesNoTrackerAtAll()
    {
        // A trackerless magnet found over DHT. Nothing about it is private.
        new PrivateTrackerRegistry([Seeding()]).OriginFor([]).Should().Be(TorrentOrigin.Public);
    }

    [Fact]
    public void OriginFor_RecognisesAConfiguredTrackerByItsHost()
    {
        PrivateTrackerRegistry registry = new([Seeding()]);

        // The passkey in an announce URL differs per user and per torrent on some
        // trackers, so the host is what identifies it, not the whole string.
        registry.OriginFor(["https://tracker.private.test/announce?passkey=totally-different"])
            .Should().Be(TorrentOrigin.PrivateSeeding);
    }

    [Fact]
    public void OriginFor_IsPrivateWithoutSeedingWhenSeedingIsOff()
    {
        PrivateTrackerRegistry registry = new([Leeching()]);

        registry.OriginFor(["https://tracker.other.test/announce?passkey=zzz"])
            .Should().Be(TorrentOrigin.PrivateWithoutSeeding);
    }

    [Fact]
    public void OriginFor_TakesTheSeedingTrackerWhenATorrentIsOnBoth()
    {
        // A torrent listed on two private trackers, one of which we seed on. Seeding
        // satisfies both accounts, so it wins.
        PrivateTrackerRegistry registry = new([Leeching(), Seeding()]);

        registry.OriginFor(
        [
            "https://tracker.other.test/announce?passkey=zzz",
            "https://tracker.private.test/announce?passkey=abc123",
        ]).Should().Be(TorrentOrigin.PrivateSeeding);
    }

    [Fact]
    public void OriginFor_IgnoresCaseInTheHost()
    {
        new PrivateTrackerRegistry([Seeding()])
            .OriginFor(["https://Tracker.Private.TEST/announce"])
            .Should().Be(TorrentOrigin.PrivateSeeding);
    }

    [Fact]
    public void PolicyFor_CarriesTheTrackersOwnTargets()
    {
        PrivateTrackerRegistry registry = new([Seeding()]);

        SwarmPolicy policy = registry.PolicyFor(SwarmPolicy.Default, ["https://tracker.private.test/announce"]);

        policy.SeedRatioTarget.Should().Be(2.0);
        policy.SeedTimeTarget.Should().Be(TimeSpan.FromHours(48));
    }

    [Fact]
    public void PolicyFor_LeavesAPublicTorrentOnTheDefaults()
    {
        PrivateTrackerRegistry registry = new([Seeding()]);

        SwarmPolicy policy = registry.PolicyFor(SwarmPolicy.Default, ["udp://open.tracker.test:1337/announce"]);

        policy.Should().Be(SwarmPolicy.Default);
    }

    [Fact]
    public void Constructor_RejectsATrackerWithoutAUsableAnnounceUrl()
    {
        Action bad = () => _ = new PrivateTrackerRegistry([new PrivateTracker { Name = "x", AnnounceUrl = "not a url" }]);

        bad.Should().Throw<ArgumentException>();
    }
}
