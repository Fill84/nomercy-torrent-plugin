// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class TorznabIndexerTests
{
    private const string Empty = """
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel />
        </rss>
        """;

    private const string Response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Silo S03E04 1080p WEB H264-CAKES</title>
              <guid>https://indexer.example/details/1</guid>
              <size>1503238553</size>
              <link>https://indexer.example/download/1.torrent</link>
              <torznab:attr name="seeders" value="42" />
              <torznab:attr name="peers" value="50" />
              <torznab:attr name="infohash" value="ABCDEF0123456789ABCDEF0123456789ABCDEF01" />
            </item>
          </channel>
        </rss>
        """;

    private static TorznabIndexer Indexer(
        StubHttpMessageHandler handler,
        IReadOnlyList<int>? categories = null
    ) =>
        new(
            "prowlarr",
            9,
            new Uri("https://indexer.example/api"),
            "SECRETKEY",
            new ChallengeAwareFetch(handler.Client(), new ClearanceStore(() => DateTimeOffset.UtcNow)),
            categories
        );

    [Fact]
    public async Task SearchAsync_UsesTvSearchWithSeasonAndEpisodeWhenASlotIsWanted()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler)
            .SearchAsync(new SearchQuery("Silo", new EpisodeSlot(3, 4)), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("t=tvsearch");
        url.Should().Contain("q=Silo");
        url.Should().Contain("season=3");
        url.Should().Contain("ep=4");
    }

    [Fact]
    public async Task SearchAsync_FallsBackToAPlainSearchWithoutASlot()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("t=search");
        url.Should().NotContain("season=");
        url.Should().NotContain("ep=");
    }

    [Fact]
    public async Task SearchAsync_SendsTheApiKeyAndConfiguredCategories()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler, [5030, 5040])
            .SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        string url = handler.Requests.Single().ToString();
        url.Should().Contain("apikey=SECRETKEY");
        url.Should().Contain("cat=5030,5040");
    }

    [Fact]
    public async Task SearchAsync_EscapesTheQueryText()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);

        await Indexer(handler)
            .SearchAsync(new SearchQuery("It's Always Sunny"), CancellationToken.None);

        // AbsoluteUri, not ToString(): ToString() returns a display form that unescapes %20 back
        // to a literal space, so asserting on it would fail against correctly escaped output.
        handler.Requests.Single().AbsoluteUri.Should().NotContain(" ");
        handler.Requests.Single().AbsoluteUri.Should().Contain("%20");
    }

    [Fact]
    public async Task SearchAsync_NeverPutsTheApiKeyInAnExceptionMessage()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            "nope",
            HttpStatusCode.Unauthorized
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should()
            .NotContain("SECRETKEY");
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionWhenTheRequestFails()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Throwing(
            new HttpRequestException("dns")
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        (await act.Should().ThrowAsync<IndexerException>()).And.Message.Should()
            .NotContain("SECRETKEY");
    }

    [Fact]
    public async Task SearchAsync_ReturnsTheReleasesParsedFromARealResponse()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Response);

        ReleaseInfo release = (
            await Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None)
        ).Single();

        release.Title.Should().Be("Silo S03E04 1080p WEB H264-CAKES");
        release.Seeders.Should().Be(42);
        release.DownloadUrl.Should().Be("https://indexer.example/download/1.torrent");
        release.IndexerName.Should().Be("prowlarr");
        release.IndexerPriority.Should().Be(9);
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
    public async Task SearchAsync_ReplacesAnExistingQueryOnTheBaseUrlInsteadOfAppendingToIt()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(Empty);
        TorznabIndexer indexer = new(
            "prowlarr",
            9,
            new Uri("https://jackett.local/api/v2.0/indexers/x/results/torznab/?apikey=OLD"),
            "NEW",
            new ChallengeAwareFetch(handler.Client(), new ClearanceStore(() => DateTimeOffset.UtcNow))
        );

        await indexer.SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        string url = handler.Requests.Single().AbsoluteUri;
        url.Count(c => c == '?').Should().Be(1);
        url.Split("apikey=").Length.Should().Be(2);
        url.Should().Contain("apikey=NEW");
        url.Should().Contain("t=search");
    }

    [Fact]
    public async Task SearchAsync_ThrowsIndexerExceptionOnAMalformedCharset()
    {
        StubHttpMessageHandler handler = StubHttpMessageHandler.Returning(
            Empty,
            contentType: "text/xml; charset=utf8"
        );

        Func<Task> act = () =>
            Indexer(handler).SearchAsync(new SearchQuery("Silo"), CancellationToken.None);

        await act.Should().ThrowAsync<IndexerException>();
    }
}
