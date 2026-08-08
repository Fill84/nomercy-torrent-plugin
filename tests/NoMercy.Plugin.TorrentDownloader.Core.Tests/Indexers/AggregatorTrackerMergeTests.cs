// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class AggregatorTrackerMergeTests
{
    private const string Hash = "123456789abcdef00020417e2d5f2e7aff010203";

    private static ReleaseInfo Release(string indexer, int priority, params string[] trackers) => new()
    {
        IndexerName = indexer,
        TorrentId = indexer + "-1",
        Title = "Some.Show.S01E01.1080p.WEB-DL",
        InfoHash = Hash,
        MagnetUri = $"magnet:?xt=urn:btih:{Hash}" + string.Concat(trackers.Select(t => "&tr=" + Uri.EscapeDataString(t))),
        SizeBytes = 2_000_000_000,
        Seeders = 40,
        IndexerPriority = priority,
    };

    private static PacedIndexer Paced(string name, int priority, ReleaseInfo release) =>
        new(new StubIndexer(name, priority, release), new IndexerPacer(new FakeClock(DateTimeOffset.UnixEpoch), TimeSpan.Zero, 4, 3, TimeSpan.Zero));

    [Fact]
    public async Task SearchAsync_GivesTheWinningReleaseEveryTrackerItsDuplicatesNamed()
    {
        IndexerAggregator aggregator = new(
        [
            Paced("site-a", 1, Release("site-a", 1, "udp://one.test:1337/announce")),
            Paced("site-b", 5, Release("site-b", 5, "udp://two.test:1337/announce")),
            Paced("site-c", 3, Release("site-c", 3, "http://three.test/announce", "udp://one.test:1337/announce")),
        ]);

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Some Show"), CancellationToken.None);

        // One info hash across three sites is still one release - but it now announces
        // to every tracker all three of them knew about.
        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("site-b", "the highest priority indexer still wins the row");
        result.Releases[0].Trackers.Should().BeEquivalentTo(
        [
            "udp://one.test:1337/announce",
            "udp://two.test:1337/announce",
            "http://three.test/announce",
        ]);
    }

    [Fact]
    public async Task SearchAsync_LeavesADistinctReleaseWithOnlyItsOwnTrackers()
    {
        ReleaseInfo other = Release("site-b", 5, "udp://two.test:1337/announce") with
        {
            Title = "Some.Other.Show.S02E03.1080p",
            InfoHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            MagnetUri = "magnet:?xt=urn:btih:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa&tr=" +
                Uri.EscapeDataString("udp://two.test:1337/announce"),
        };

        IndexerAggregator aggregator = new(
        [
            Paced("site-a", 1, Release("site-a", 1, "udp://one.test:1337/announce")),
            Paced("site-b", 5, other),
        ]);

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Some Show"), CancellationToken.None);

        result.Releases.Should().HaveCount(2);
        result.Releases.Should().OnlyContain(release => release.Trackers.Count == 1);
    }

    private sealed class StubIndexer(string name, int priority, ReleaseInfo release) : IIndexer
    {
        public string Name => name;

        public int Priority => priority;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ReleaseInfo>>([release]);
    }
}
