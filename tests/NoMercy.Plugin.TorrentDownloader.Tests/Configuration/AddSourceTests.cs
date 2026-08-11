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

    /// <summary>
    /// The paste an owner actually makes.
    ///
    /// <para>
    /// Observed on the real server: the address bar after searching a site by hand reads
    /// <c>https://extranet.torrentbay.st/browse/?q=</c> once the term is cleared, and that is
    /// what gets pasted. The empty value of the last query parameter is unambiguously where
    /// the search terms go - there is nowhere else they could go - so filling it in beats
    /// refusing a paste that is right in every way but one.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://extranet.torrentbay.st/browse/?q=", "https://extranet.torrentbay.st/browse/?q={query}")]
    [InlineData("https://site.test/search?type=tv&q=", "https://site.test/search?type=tv&q={query}")]
    public async Task HandleAddSourceAsync_FillsInTheSearchTermsWhereThePasteLeftThemBlank(string pasted, string stored)
    {
        (await AddAsync("A site", "site", pasted)).Succeeded.Should().BeTrue();

        Saved().Indexers.Should().ContainSingle().Which.Url.Should().Be(stored);
    }

    /// <summary>
    /// Any site, in whatever way the owner wrote down where the terms go.
    ///
    /// <para>
    /// The two real addresses this was asked for put the terms in different places - one in
    /// a query parameter, one in the path - which is exactly why the marker is the owner's
    /// to place and not something the plugin can work out. What it should not do is insist
    /// on one spelling of the marker.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData("https://extranet.torrentbay.st/browse/?q=<replace>", "https://extranet.torrentbay.st/browse/?q={query}")]
    [InlineData("https://www.limetorrents.fun/search/all/<replace>", "https://www.limetorrents.fun/search/all/{query}")]
    [InlineData("https://site.test/search/%s", "https://site.test/search/{query}")]
    [InlineData("https://site.test/search/{search}", "https://site.test/search/{query}")]
    [InlineData("https://site.test/search/<QUERY>", "https://site.test/search/{query}")]
    [InlineData("https://site.test/search/{query}", "https://site.test/search/{query}")]
    public async Task HandleAddSourceAsync_TakesTheMarkerHoweverTheOwnerWroteIt(string written, string stored)
    {
        (await AddAsync("A site", "site", written)).Succeeded.Should().BeTrue();

        Saved().Indexers.Should().ContainSingle().Which.Url.Should().Be(stored);
    }

    // Only the blank value at the end. Anything else is a guess about which part of somebody
    // else's URL means "the thing I searched for", and a wrong guess searches the wrong page
    // forever without ever looking broken.
    [Fact]
    public async Task HandleAddSourceAsync_DoesNotGuessWhereTermsGoInAnOrdinaryPath()
    {
        SaveSettingsOutcome outcome = await AddAsync("A site", "site", "https://site.test/browse/tv/");

        outcome.Succeeded.Should().BeFalse();
        outcome.Error.Should().Contain("{query}");
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
