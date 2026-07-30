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

    private static TorznabIndexer Indexer(
        StubHttpMessageHandler handler,
        IReadOnlyList<int>? categories = null
    ) =>
        new(
            "prowlarr",
            9,
            new Uri("https://indexer.example/api"),
            "SECRETKEY",
            handler.Client(),
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
}
