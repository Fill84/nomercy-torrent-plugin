// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Views;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// Saving a site: the one field an owner cannot guess, checked while they are looking at it.
/// </summary>
public class SiteIndexerSaveTests
{
    // A site address without the placeholder searches the same page for every query, so
    // every episode gets the same rows back. That looks like a working site with bad
    // results, and nobody would think to blame a missing word in a settings box.
    [Fact]
    public void TheKindSelectOffersAllThreeKinds()
    {
        TorrentDownloaderSettings settings = new();
        settings.Indexers.Add(new IndexerSettings { Name = "A site", Kind = "site", Url = "https://x.test/s/{query}/" });

        string page = Render(settings);

        page.Should().Contain("\"site\"").And.Contain("\"rss\"").And.Contain("\"torznab\"");
    }

    // The label has to ask for the thing itself, because "URL" invites the front page.
    [Fact]
    public void ASiteIsAskedForItsSearchAddressRatherThanItsUrl()
    {
        TorrentDownloaderSettings settings = new();
        settings.Indexers.Add(new IndexerSettings { Name = "A site", Kind = "site", Url = "https://x.test/s/{query}/" });

        Render(settings).Should().Contain("{query}");
    }

    [Fact]
    public void AFeedIsStillJustAskedForAUrl()
    {
        TorrentDownloaderSettings settings = new();
        settings.Indexers.Add(new IndexerSettings { Name = "A feed", Kind = "rss", Url = "https://x.test/rss" });

        Render(settings).Should().Contain("\"URL\"");
    }

    private static string Render(TorrentDownloaderSettings settings) =>
        System.Text.Json.JsonSerializer.Serialize(SettingsView.Build(settings, [], new HashSet<string>()));
}
