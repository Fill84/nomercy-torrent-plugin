// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>
/// Adding a source complete, in one go, from wherever the owner happens to be.
/// </summary>
public class AddSourceTests
{
    private readonly FakeConfiguration _configuration = new();
    private readonly FakeSecretStore _secrets = new();

    private SettingsSaveHandler Handler() =>
        new(new SettingsGateway(_configuration, _secrets), new FakeClock(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

    private async Task<SaveSettingsOutcome> AddAsync(string? name, string? kind, string? url) =>
        await Handler().HandleAddSourceAsync(new SaveSettingsRequest { Name = name, Kind = kind, Url = url });

    private TorrentDownloaderSettings Saved() =>
        _configuration.GetConfiguration<TorrentDownloaderSettings>() ?? new TorrentDownloaderSettings();

    // The settings page adds a blank row to fill in later. That is fine on the settings
    // page and wrong everywhere else: the moment somebody needs another source is the
    // moment an episode found nothing, and they are not looking at the settings then.
    [Fact]
    public async Task HandleAddSourceAsync_AddsAFeedReadyToUse()
    {
        (await AddAsync("SceneSource", "rss", "https://scnsrc.me/feed")).Succeeded.Should().BeTrue();

        IndexerSettings added = Saved().Indexers.Should().ContainSingle().Subject;
        added.Name.Should().Be("SceneSource");
        added.Kind.Should().Be("rss");
        added.Url.Should().Be("https://scnsrc.me/feed");
        added.Enabled.Should().BeTrue("a source nobody enabled is a source that does nothing");
    }

    [Fact]
    public async Task HandleAddSourceAsync_AddsASiteWithItsSearchAddress()
    {
        (await AddAsync("A site", "site", "https://site.test/search/{query}/")).Succeeded.Should().BeTrue();

        Saved().Indexers.Should().ContainSingle().Which.Kind.Should().Be("site");
    }

    // Without the placeholder it searches the same page for every query, which reads as a
    // working site with bad results rather than as a setting nobody filled in.
    [Fact]
    public async Task HandleAddSourceAsync_RefusesASiteWithNoPlaceholderInIt()
    {
        SaveSettingsOutcome outcome = await AddAsync("A site", "site", "https://site.test/search/");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("{query}");
        Saved().Indexers.Should().BeEmpty();
    }

    [Fact]
    public async Task HandleAddSourceAsync_RefusesAnAddressThatIsNotAUrl()
    {
        (await AddAsync("Whoops", "rss", "scnsrc.me")).Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task HandleAddSourceAsync_RefusesANameThatIsAlreadyTaken()
    {
        await AddAsync("SceneSource", "rss", "https://scnsrc.me/feed");

        SaveSettingsOutcome outcome = await AddAsync("scenesource", "rss", "https://other.test/feed");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("already");
        Saved().Indexers.Should().ContainSingle("the second one was refused, not merged");
    }

    // The secrets belong to the indexer by name, so adding one must not disturb the rest.
    [Fact]
    public async Task HandleAddSourceAsync_LeavesTheSourcesAlreadyThereAlone()
    {
        await AddAsync("First", "rss", "https://one.test/feed");
        await AddAsync("Second", "site", "https://two.test/s/{query}");

        Saved().Indexers.Select(indexer => indexer.Name).Should().Equal("First", "Second");
    }
}
