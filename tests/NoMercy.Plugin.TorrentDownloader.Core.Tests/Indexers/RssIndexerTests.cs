// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class RssIndexerTests
{
    private static RssIndexer Indexer(
        StubHttpMessageHandler handler,
        IReadOnlyList<string>? categories = null
    ) =>
        new("scnsrc", 5, new Uri("https://feed.example/rss"), handler.Client(), categories);

    [Fact]
    public async Task SearchAsync_ReturnsEveryItemFromTheRealCapture()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().HaveCount(40);
        results.Should().OnlyContain(release => release.IndexerName == "scnsrc");
        results.Should().OnlyContain(release => release.IndexerPriority == 5);
    }

    [Fact]
    public async Task SearchAsync_KeepsOnlyTheConfiguredCategories()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler, ["TV"])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().HaveCount(21);
    }

    [Fact]
    public async Task SearchAsync_MarksSceneFeedItemsAsDiscoveryOnly()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Fixtures.Text("scnsrc-feed.xml")
        );

        IReadOnlyList<ReleaseInfo> results = await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        results.Should().OnlyContain(release => release.MagnetUri == null && release.DownloadUrl == null);
    }

    [Fact]
    public async Task SearchAsync_ReadsAnEnclosureAsADownloadUrlAndSize()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>https://tracker.example/t/1</link>
                <enclosure url="https://tracker.example/t/1.torrent"
                           length="1503238553"
                           type="application/x-bittorrent" />
              </item>
            </channel></rss>
            """;

        ReleaseInfo release = (
            await Indexer(StubHttpMessageHandler.Returning(xml))
                .SearchAsync(new SearchQuery("Silo"), CancellationToken.None)
        ).Single();

        release.DownloadUrl.Should().Be("https://tracker.example/t/1.torrent");
        release.SizeBytes.Should().Be(1503238553L);
        release.DetailUrl.Should().Be("https://tracker.example/t/1");
    }

    [Fact]
    public async Task SearchAsync_ReadsAMagnetLinkAndItsInfoHash()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>magnet:?xt=urn:btih:ABCDEF0123456789ABCDEF0123456789ABCDEF01&amp;dn=Silo</link>
              </item>
            </channel></rss>
            """;

        ReleaseInfo release = (
            await Indexer(StubHttpMessageHandler.Returning(xml))
                .SearchAsync(new SearchQuery("Silo"), CancellationToken.None)
        ).Single();

        release.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:");
        release.InfoHash.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
        release.DetailUrl.Should().BeNull();
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionOnAnErrorStatus()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            "nope",
            HttpStatusCode.ServiceUnavailable
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>().WithMessage("*503*");
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionWhenTheRequestFails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("dns")
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>();
    }

    [Fact]
    public async Task SearchAsync_LetsCallerCancellationPropagate()
    {
        using CancellationTokenSource source = new();
        await source.CancelAsync();

        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new OperationCanceledException()
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), source.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task SearchAsync_WrapsATimeoutThatTheCallerDidNotRequest()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new OperationCanceledException()
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>();
    }
}
