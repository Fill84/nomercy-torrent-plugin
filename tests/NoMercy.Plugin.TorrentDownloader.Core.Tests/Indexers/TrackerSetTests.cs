// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class TrackerSetTests
{
    private const string Hash = "123456789abcdef00020417e2d5f2e7aff010203";

    private static ReleaseInfo From(string indexer, params string[] trackers)
    {
        string magnet = $"magnet:?xt=urn:btih:{Hash}&dn=Some.Show.S01E01" +
            string.Concat(trackers.Select(tracker => "&tr=" + Uri.EscapeDataString(tracker)));

        return new ReleaseInfo
        {
            IndexerName = indexer,
            TorrentId = indexer + "-1",
            Title = "Some.Show.S01E01.1080p",
            InfoHash = Hash,
            MagnetUri = magnet,
            SizeBytes = 1000,
            Seeders = 10,
        };
    }

    [Fact]
    public void Merge_TakesTheUnionOfEverySourcesTrackers()
    {
        // The same release listed on three sites announces three different tracker
        // sets. One info hash deserves all of them: a bigger swarm is a faster download.
        IReadOnlyList<string> merged = TrackerSet.Merge(
        [
            From("site-a", "udp://one.test:1337/announce"),
            From("site-b", "udp://two.test:1337/announce", "http://three.test/announce"),
            From("site-c", "udp://one.test:1337/announce"),
        ]);

        merged.Should().BeEquivalentTo(
        [
            "udp://one.test:1337/announce",
            "udp://two.test:1337/announce",
            "http://three.test/announce",
        ]);
    }

    [Fact]
    public void Merge_KeepsOneEntryForTheSameTrackerWrittenDifferently()
    {
        IReadOnlyList<string> merged = TrackerSet.Merge(
        [
            From("site-a", "udp://Tracker.Test:1337/announce"),
            From("site-b", "udp://tracker.test:1337/announce/"),
            From("site-c", "UDP://tracker.test:1337/announce"),
        ]);

        merged.Should().ContainSingle();
    }

    [Fact]
    public void Merge_KeepsTrackersThatOnlyDifferByPortOrScheme()
    {
        // A tracker on UDP and on HTTP is two ways in, not one written twice.
        IReadOnlyList<string> merged = TrackerSet.Merge(
        [
            From("site-a", "udp://tracker.test:1337/announce"),
            From("site-b", "http://tracker.test/announce"),
            From("site-c", "udp://tracker.test:2710/announce"),
        ]);

        merged.Should().HaveCount(3);
    }

    [Fact]
    public void Merge_IgnoresAReleaseWithNoMagnet()
    {
        ReleaseInfo withoutMagnet = From("site-a") with { MagnetUri = null };

        IReadOnlyList<string> merged = TrackerSet.Merge([withoutMagnet, From("site-b", "udp://one.test:1337/announce")]);

        merged.Should().ContainSingle().Which.Should().Be("udp://one.test:1337/announce");
    }

    [Fact]
    public void Merge_IgnoresSomethingThatIsNotAMagnetAtAll()
    {
        ReleaseInfo broken = From("site-a") with { MagnetUri = "http://example.test/not-a-magnet" };

        TrackerSet.Merge([broken]).Should().BeEmpty();
    }

    [Fact]
    public void Merge_ReturnsNothingWhenNobodyNamedATracker()
    {
        // Normal for a magnet meant to be found over DHT.
        TrackerSet.Merge([From("site-a"), From("site-b")]).Should().BeEmpty();
    }

    [Fact]
    public void Merge_KeepsTheOrderItFirstSawEachTracker()
    {
        IReadOnlyList<string> merged = TrackerSet.Merge(
        [
            From("site-a", "udp://first.test:1337/announce"),
            From("site-b", "udp://second.test:1337/announce"),
        ]);

        merged.Should().Equal("udp://first.test:1337/announce", "udp://second.test:1337/announce");
    }
}
