// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;

/// <summary>
/// Where to get a release the feed only named.
/// </summary>
public class ReleaseResolverTests
{
    private const string Announced = "Some.Show.S01E01.1080p.WEB-DL-GROUP";

    private static ReleaseInfo Row(string title, string hash, int seeders = 10, int priority = 25) => new()
    {
        IndexerName = $"site-{hash}",
        TorrentId = hash,
        Title = title,
        InfoHash = hash,
        MagnetUri = $"magnet:?xt=urn:btih:{hash}",
        Seeders = seeders,
        IndexerPriority = priority,
    };

    private static IndexerReleaseResolver Resolver(params ReleaseInfo[] rows) =>
        new([new PacedIndexer(new StubIndexer(rows), Unpaced())]);

    private static IndexerPacer Unpaced() =>
        new(new SystemClock(), TimeSpan.Zero, maxConcurrency: 4, failureThreshold: 3, cooldown: TimeSpan.Zero);

    // The ranking is not the quality profile. The profile already chose which release is
    // wanted; this only chooses where to get that one, so a different release is not a
    // better answer - it is an answer to a different question.
    [Fact]
    public async Task ResolveAsync_TheExactAnnouncedTitleWinsOverABetterSeededDifferentRelease()
    {
        IndexerReleaseResolver resolver = Resolver(
            Row("Some.Show.S01E01.2160p.REMUX-OTHER", "wrong", seeders: 900),
            Row(Announced, "right", seeders: 4));

        ReleaseInfo? resolved = await resolver.ResolveAsync(Row(Announced, "x") with { MagnetUri = null }, CancellationToken.None);

        resolved!.InfoHash.Should().Be("right");
    }

    [Fact]
    public async Task ResolveAsync_PrefersTheHigherPrioritySiteWhenBothHaveTheRelease()
    {
        IndexerReleaseResolver resolver = Resolver(
            Row(Announced, "second", priority: 50),
            Row(Announced, "first", priority: 10));

        ReleaseInfo? resolved = await resolver.ResolveAsync(Row(Announced, "x"), CancellationToken.None);

        resolved!.InfoHash.Should().Be("first");
    }

    [Fact]
    public async Task ResolveAsync_FallsBackToSeedersWhenTitleAndPriorityTie()
    {
        IndexerReleaseResolver resolver = Resolver(
            Row(Announced, "thin", seeders: 2),
            Row(Announced, "fat", seeders: 200));

        (await resolver.ResolveAsync(Row(Announced, "x"), CancellationToken.None))!.InfoHash.Should().Be("fat");
    }

    // A row nobody can download is not an answer to "where do I get this".
    [Fact]
    public async Task ResolveAsync_IgnoresRowsWithNothingToDownload()
    {
        IndexerReleaseResolver resolver = Resolver(Row(Announced, "empty") with { MagnetUri = null, DownloadUrl = null });

        (await resolver.ResolveAsync(Row(Announced, "x"), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_SaysNoWhenNoSiteHasIt()
    {
        (await Resolver().ResolveAsync(Row(Announced, "x"), CancellationToken.None)).Should().BeNull();
    }

    private sealed class StubIndexer(IReadOnlyList<ReleaseInfo> rows) : IIndexer
    {
        public string Name => "stub";
        public int Priority => 25;

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct) =>
            Task.FromResult(rows);
    }
}
