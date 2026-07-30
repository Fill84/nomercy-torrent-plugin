// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class IndexerAggregatorTests
{
    private sealed class StubIndexer(string name, int priority, params ReleaseInfo[] results) : IIndexer
    {
        public string Name => name;
        public int Priority => priority;
        public int Calls { get; private set; }

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(results);
        }
    }

    private sealed class FailingIndexer(string name) : IIndexer
    {
        public string Name => name;
        public int Priority => 0;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            throw new IndexerException($"{name}: search returned HTTP 500");
    }

    private sealed class CrashingIndexer(string name) : IIndexer
    {
        public string Name => name;
        public int Priority => 0;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }

    private static ReleaseInfo Release(string title, string indexer, int priority, string? hash = null, int seeders = 10) =>
        new()
        {
            IndexerName = indexer,
            TorrentId = title + indexer,
            Title = title,
            InfoHash = hash,
            Seeders = seeders,
            IndexerPriority = priority,
        };

    private static PacedIndexer Paced(IIndexer indexer, FakeClock clock) =>
        new(indexer, new IndexerPacer(clock, TimeSpan.Zero, 4, 99, TimeSpan.FromMinutes(5)));

    [Fact]
    public async Task SearchAsync_MergesResultsFromEveryIndexer()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("a", 1, Release("Silo S03E04 1080p", "a", 1, "aaa")), clock),
                Paced(new StubIndexer("b", 2, Release("Silo S03E05 1080p", "b", 2, "bbb")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().HaveCount(2);
        result.Failures.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchAsync_KeepsGoingWhenOneIndexerFails()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new FailingIndexer("broken"), clock),
                Paced(new StubIndexer("good", 1, Release("Silo S03E04 1080p", "good", 1, "aaa")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Failures.Should().ContainSingle();
        result.Failures[0].IndexerName.Should().Be("broken");
        result.Failures[0].Reason.Should().Contain("500");
    }

    [Fact]
    public async Task SearchAsync_ReturnsNoReleasesAndAllFailuresWhenEveryIndexerFails()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [Paced(new FailingIndexer("x"), clock), Paced(new FailingIndexer("y"), clock)]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().HaveCount(2);
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesOnInfoHashKeepingTheHigherPriorityIndexer()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("low", 1, Release("Silo S03E04 1080p", "low", 1, "SAMEHASH", seeders: 5)), clock),
                Paced(new StubIndexer("high", 9, Release("Silo S03E04 1080p", "high", 9, "samehash", seeders: 50)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("high");
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesOnNormalisedTitleWhenNoInfoHashIsReported()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("low", 1, Release("Silo.S03E04.1080p.WEB.H264-CAKES", "low", 1)), clock),
                Paced(new StubIndexer("high", 9, Release("Silo S03E04 1080p WEB H264 CAKES", "high", 9)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("high");
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesAcrossIndexersWhenOnlyOneReportsAnInfoHash()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("hashed", 1, Release("Silo.S03E04.1080p.WEB.H264-CAKES", "hashed", 1, "SAMEHASH")), clock),
                Paced(new StubIndexer("hashless", 2, Release("Silo S03E04 1080p WEB H264 CAKES", "hashless", 2)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchAsync_DeduplicatesAcrossIndexersWhenOnlyOneReportsAnInfoHash_ReverseOrder()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("hashless", 2, Release("Silo S03E04 1080p WEB H264 CAKES", "hashless", 2)), clock),
                Paced(new StubIndexer("hashed", 1, Release("Silo.S03E04.1080p.WEB.H264-CAKES", "hashed", 1, "SAMEHASH")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
    }

    private static ReleaseInfo ReleaseWithDownload(string title, string indexer, int priority, string? downloadUrl) =>
        new()
        {
            IndexerName = indexer,
            TorrentId = title + indexer,
            Title = title,
            DownloadUrl = downloadUrl,
            IndexerPriority = priority,
        };

    [Fact]
    public async Task SearchAsync_AtEqualPriorityPrefersTheGrabbableCopyOverTheDiscoveryOnlyOne()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("scnsrc", 5, ReleaseWithDownload("Silo.S03E04.1080p.WEB.H264-CAKES", "scnsrc", 5, null)), clock),
                Paced(new StubIndexer("prowlarr", 5, ReleaseWithDownload("Silo S03E04 1080p WEB H264 CAKES", "prowlarr", 5, "https://indexer.example/download/1.torrent")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("prowlarr");
        result.Releases[0].DownloadUrl.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_AtEqualPriorityPrefersTheGrabbableCopyOverTheDiscoveryOnlyOne_ReverseOrder()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new StubIndexer("prowlarr", 5, ReleaseWithDownload("Silo S03E04 1080p WEB H264 CAKES", "prowlarr", 5, "https://indexer.example/download/1.torrent")), clock),
                Paced(new StubIndexer("scnsrc", 5, ReleaseWithDownload("Silo.S03E04.1080p.WEB.H264-CAKES", "scnsrc", 5, null)), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Releases[0].IndexerName.Should().Be("prowlarr");
        result.Releases[0].DownloadUrl.Should().NotBeNull();
    }

    [Fact]
    public async Task SearchAsync_ReportsAParkedIndexerAsAFailureWithoutCallingIt()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        StubIndexer stub = new("parked", 1, Release("Silo S03E04 1080p", "parked", 1, "aaa"));
        IndexerPacer pacer = new(clock, TimeSpan.Zero, 4, failureThreshold: 1, cooldown: TimeSpan.FromMinutes(5));

        Func<Task> trip = () =>
            pacer.RunAsync<int>(_ => throw new IndexerException("boom"), CancellationToken.None);
        await trip.Should().ThrowAsync<IndexerException>();

        IndexerAggregator aggregator = new([new PacedIndexer(stub, pacer)]);

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().ContainSingle().Which.Reason.Should().Contain("parked");
        stub.Calls.Should().Be(0);
    }

    [Fact]
    public async Task SearchAsync_RecordsANonIndexerExceptionAsAFailureNamingItsType()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        IndexerAggregator aggregator = new(
            [
                Paced(new CrashingIndexer("crashy"), clock),
                Paced(new StubIndexer("good", 1, Release("Silo S03E04 1080p", "good", 1, "aaa")), clock),
            ]
        );

        AggregateResult result = await aggregator.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().ContainSingle();
        result.Failures.Should().ContainSingle();
        result.Failures[0].IndexerName.Should().Be("crashy");
        result.Failures[0].Reason.Should().Contain(nameof(InvalidOperationException));
    }

    [Fact]
    public async Task SearchAsync_LetsCallerCancellationPropagateRatherThanReportingAFailure()
    {
        FakeClock clock = new(DateTimeOffset.UnixEpoch);
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        IndexerAggregator aggregator = new(
            [Paced(new StubIndexer("good", 1, Release("Silo S03E04 1080p", "good", 1, "aaa")), clock)]
        );

        Func<Task> act = () => aggregator.SearchAsync(new SearchQuery("Silo"), source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchAsync_ReturnsEmptyWithoutFailuresWhenNoIndexersAreConfigured()
    {
        AggregateResult result = await new IndexerAggregator([])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        result.Releases.Should().BeEmpty();
        result.Failures.Should().BeEmpty();
    }
}
