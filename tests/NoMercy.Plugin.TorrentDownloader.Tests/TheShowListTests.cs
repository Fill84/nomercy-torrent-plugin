using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The show list through the plugin as the server loads it: a row switched, a form saved and read
/// back, a refused form, and a library's preferences followed. <c>docs/specs/show-list.md</c>.
/// </summary>
public sealed class TheShowListTests : IDisposable
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    /// <summary>A broadcast day well in the past, so an episode without a file is a gap.</summary>
    private static readonly DateTime Aired = DateTime.UtcNow.AddMonths(-2);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", "show-list-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// On is on. There is no "On, not saved" any more: a show switched on from its row follows its library
    /// until the owner changes the show itself (the owner's rule of 16 September 2026).
    /// </remarks>
    [Fact]
    public async Task AShowSwitchedOnFromItsRowIsOn()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("Off", await StateOf(plugin, 41));

        await plugin.SwitchShowAsync(41, on: true, CancellationToken.None);

        Assert.Equal("On", await StateOf(plugin, 41));
    }

    [Fact]
    public async Task ASavedFormIsWhatTheShowsPageHoldsAfterward()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        IReadOnlyList<string> refused = await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?>
            {
                ["switchedOn"] = "true",
                ["quality"] = "2160p",
                ["wishes"] = "WEB",
                ["wishes.add"] = "NTb",
            },
            CancellationToken.None);

        Assert.Empty(refused);
        Assert.Equal("On", await StateOf(plugin, 41));

        PluginView page = await plugin.GetViewAsync(new() { Route = "/shows/41" }, CancellationToken.None);
        IReadOnlyList<PluginFormField> fields = Assert.IsAssignableFrom<IReadOnlyList<PluginFormField>>(
            Rendered.ById(page, ShowSettingsView.FormId).Props["fields"]);

        Assert.Equal("2160p", fields.Single(field => field.Name == "quality").Value);
        Assert.Equal("WEB, NTb", fields.Single(field => field.Name == "wishes").Value);
    }

    [Fact]
    public async Task ARefusedFormSavesNothing()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        IReadOnlyList<string> refused = await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?> { ["switchedOn"] = "true", ["quality"] = "1080" },
            CancellationToken.None);

        Assert.NotEmpty(refused);
        Assert.Equal("Off", await StateOf(plugin, 41));

        // Nothing of it kept: the show still follows its library in everything.
        Assert.Null((await (await plugin.ShowSettingsAsync(CancellationToken.None)).ForAsync(41, CancellationToken.None)).Quality);
    }

    [Fact]
    public async Task AShowFollowsWhatItsLibraryWasSavedWith()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("not set", await CellOf(plugin, 41, "quality"));

        Assert.Empty(await plugin.SaveLibraryPreferencesAsync(
            Tv,
            new Dictionary<string, string?> { ["quality"] = "720p", ["codec"] = "h265" },
            CancellationToken.None));

        Assert.Equal("720p", await CellOf(plugin, 41, "quality"));
        Assert.Equal("h265", await CellOf(plugin, 41, "codec"));
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's report of 16 September 2026: "not counted" for every row, and the information
    /// is there.</strong> The column was fed from the episodes the plugin tracks, and it tracks only what
    /// is switched on, so every row of a fresh install read "not counted".
    /// </para>
    /// <para>
    /// It counts the aired episodes with no file, from the library itself. Not from the server's
    /// <c>HaveEpisodes</c>: on the owner's library that column reads nought for all 69 shows while 57 of
    /// them hold files.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheMissingColumnCountsAiredGapsEvenForAShowThatIsOff()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Equal("Off", await StateOf(plugin, 41));
        Assert.Equal("2", await CellOf(plugin, 41, "missing"));
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's report of 16 September 2026: shows they do not have, over and over.</strong>
    /// The server writes a row for every show it ever identified — twelve of the owner's sixty-nine are
    /// shows nobody added, imported on a guess (media-server #36) — and the overview listed all of them,
    /// which read as recommendations.
    /// </para>
    /// <para>
    /// A show the library holds no file of is not the owner's, unless they switched it on themselves:
    /// switching one on is how a show they are waiting for their first episode of stays on the page.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AShowTheLibraryHoldsNoFileOfIsNotListedUnlessItIsSwitchedOn()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        PluginView before = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(before), one => one.Id == "show-41");
        Assert.DoesNotContain(Rendered.All(before), one => one.Id == "show-42");

        await plugin.SwitchShowAsync(42, on: true, CancellationToken.None);

        PluginView after = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(after), one => one.Id == "show-42");
    }

    /// <remarks>
    /// <strong>English only, back on the owner's word of 16 September 2026</strong>
    /// (<c>docs/specs/show-list.md</c>). It is a setting like any other here: the show's own, or its
    /// library's while the show follows it.
    /// </remarks>
    [Fact]
    public async Task EnglishOnlyIsSavedOnAShowAndFollowedFromItsLibrary()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        Assert.Empty(await plugin.SaveLibraryPreferencesAsync(
            Tv,
            new Dictionary<string, string?> { ["quality"] = "1080p", ["englishOnly"] = "true" },
            CancellationToken.None));

        // The show sets nothing of its own, so it follows its library.
        Assert.Empty(await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?> { ["switchedOn"] = "true" },
            CancellationToken.None));

        Assert.True(await AppliedEnglishOnly(plugin, 41));

        // And its own answer overrules the library's.
        Assert.Empty(await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?> { ["switchedOn"] = "true", ["englishOnly"] = "off" },
            CancellationToken.None));

        Assert.False(await AppliedEnglishOnly(plugin, 41));
    }

    /// <remarks>
    /// The owner's report of 16 September 2026: a row of empty boxes says nothing about what belongs in
    /// them. Each tag field shows an example of the tags it takes.
    /// </remarks>
    [Fact]
    public async Task EveryTagFieldShowsAnExampleOfWhatGoesInIt()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        PluginView page = await plugin.GetViewAsync(new() { Route = "/shows/41" }, CancellationToken.None);
        IReadOnlyList<PluginFormField> fields = Assert.IsAssignableFrom<IReadOnlyList<PluginFormField>>(
            Rendered.ById(page, ShowSettingsView.FormId).Props["fields"]);

        Assert.All(
            fields.Where(field => field.Name.StartsWith("wishes", StringComparison.Ordinal)
                                  || field.Name.StartsWith("musts", StringComparison.Ordinal)
                                  || field.Name.StartsWith("forbidden", StringComparison.Ordinal)),
            field => Assert.False(string.IsNullOrWhiteSpace(field.Placeholder), $"{field.Name} shows no example."));
    }

    [Fact]
    public async Task AShowThatIsInNoLibrarySaysSo()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        PluginView page = await plugin.GetViewAsync(new() { Route = "/shows/999" }, CancellationToken.None);

        Assert.Contains(Rendered.Words(page), word => word.Contains("999", StringComparison.Ordinal));
        Assert.DoesNotContain(Rendered.All(page), component => component.Component == Ui.FormComponent);
    }

    /// <remarks>
    /// The missing column reads a show's files only to find an episode registered against another row, so
    /// a show with no aired gap has nothing to look for there. Reading them for every show put a question
    /// to the library for each of the owner's sixty-nine shows every time the overview was drawn.
    /// </remarks>
    [Fact]
    public async Task AShowWithNoAiredGapHasItsFilesLeftUnread()
    {
        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library(Tv, "Series", "tv")
            .Show(41, "Silo", Tv, year: 2023)
            .Episode(41, 1, 1, airDate: Aired, hasFile: true)
            .Episode(41, 1, 2, airDate: DateTime.UtcNow.AddMonths(1), hasFile: false);

        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder, Shelves = shelves });

        PluginView page = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Equal("0", Assert.IsType<string>(Rendered.ById(page, "show-41").Props["missing"]));
        Assert.Equal(0, shelves.Files);
    }

    /// <remarks>
    /// <para>
    /// <strong>Through the plugin, because that is where what is looked for is kept.</strong> The owner
    /// asked for a search on 18 September 2026 with eight pages of anime and three of television in front
    /// of them. The box posts, the plugin holds the term, and every page drawn after it is narrowed by it
    /// until it is cleared.
    /// </para>
    /// <para>
    /// A show that matches is listed whether it is switched on or off, and whether the library holds a file
    /// of it or not: looking for a show the plugin is not yet downloading is the reason to look.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatIsLookedForNarrowsTheOverviewUntilItIsCleared()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        await plugin.SwitchShowAsync(42, on: true, CancellationToken.None);

        plugin.Find("brilliant");

        PluginView narrowed = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(narrowed), one => one.Id == "show-42");
        Assert.DoesNotContain(Rendered.All(narrowed), one => one.Id == "show-41");

        plugin.Find(null);

        PluginView whole = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(whole), one => one.Id == "show-41");
        Assert.Contains(Rendered.All(whole), one => one.Id == "show-42");
    }

    /// <remarks>
    /// An empty box is nothing to look for, not a search nothing matches: pressing Find on one puts the
    /// whole list back rather than emptying the page.
    /// </remarks>
    [Fact]
    public async Task AnEmptyBoxShowsEveryShowAgain()
    {
        using TorrentDownloaderPlugin plugin = Loaded();

        plugin.Find("silo");
        plugin.Find("   ");

        Assert.Null(plugin.Finding);

        PluginView page = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        Assert.Contains(Rendered.All(page), one => one.Id == "show-41");
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    private TorrentDownloaderPlugin Loaded()
    {
        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            // Silo is the owner's: one episode of it is on disk and two aired
            // gaps are not. Brilliant Minds is one of the rows the server wrote
            // on a guess — every episode aired, not one file.
            Shelves = new FakeLibraryQuery()
                .Library(Tv, "Series", "tv")
                .Show(41, "Silo", Tv, year: 2023)
                .Episode(41, 1, 1, airDate: Aired, hasFile: true)
                .Episode(41, 1, 2, airDate: Aired, hasFile: false)
                .Episode(41, 1, 3, airDate: Aired, hasFile: false)
                .Episode(41, 1, 4, airDate: DateTime.UtcNow.AddMonths(1), hasFile: false)
                .Show(42, "Brilliant Minds", Tv, year: 2024)
                .Episode(42, 1, 1, airDate: Aired, hasFile: false)
                .Episode(42, 1, 2, airDate: Aired, hasFile: false),
        });

        return plugin;
    }

    /// <summary>What applies to a show, read off its own settings page.</summary>
    private static async Task<bool> AppliedEnglishOnly(TorrentDownloaderPlugin plugin, int showId)
    {
        PluginView page = await plugin.GetViewAsync(new() { Route = $"/shows/{showId}" }, CancellationToken.None);
        string applied = Assert.IsType<string>(Rendered.ById(page, ShowSettingsView.AppliedId).Props["value"]);

        return applied.Contains("English only on", StringComparison.Ordinal);
    }

    private static async Task<string> StateOf(TorrentDownloaderPlugin plugin, int showId)
    {
        return await CellOf(plugin, showId, "state");
    }

    private static async Task<string> CellOf(TorrentDownloaderPlugin plugin, int showId, string key)
    {
        PluginView page = await plugin.GetViewAsync(new() { Route = Pages.OverviewRoute }, CancellationToken.None);

        return Assert.IsType<string>(Rendered.ById(page, $"show-{showId}").Props[key]);
    }
}
