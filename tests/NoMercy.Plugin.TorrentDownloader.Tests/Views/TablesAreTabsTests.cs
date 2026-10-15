using System.Globalization;

using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// A page with two or more tables shows them as tabs, one table at a time.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #4, 13 October 2026: the Queue, the Activity page and the overview stacked their tables
/// below each other, so the second list of the Queue began half a page down whatever the first held, and
/// a library of anime was found by scrolling past every show of the television library above it.
/// </para>
/// <para>
/// The chosen tab is in the path and never in a query: the web app hands the plugin a page's path and
/// nothing after a question mark, so a tab held in a query would be forgotten on every reload.
/// </para>
/// </remarks>
public sealed class TablesAreTabsTests : IDisposable
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string Anime = "01HQ5W4GAVF30N10RT6XCF6AJQ";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void TheQueueDrawsATabPerListNamedWithHowManyItHolds()
    {
        PluginView page = QueueView.Render(Queue());

        Assert.Equal(["Looking (2)", "Waiting to air (1)"], Labels(page));
        Assert.Equal(["/queue/looking", "/queue/waiting"], Routes(page));
    }

    [Fact]
    public void OnlyTheChosenTabsTableIsDrawnAndTheTabIsMarkedAsTheNavigationMarksItsPage()
    {
        PluginView looking = QueueView.Render(Queue());

        Assert.Contains(Rendered.All(looking), one => one.Id == QueueView.LookingTableId);
        Assert.DoesNotContain(Rendered.All(looking), one => one.Id == QueueView.WaitingTableId);
        Assert.Equal(["primary", "ghost"], Variants(looking));

        PluginView waiting = QueueView.Render(Queue(), QueueView.WaitingTableId);

        Assert.Contains(Rendered.All(waiting), one => one.Id == QueueView.WaitingTableId);
        Assert.DoesNotContain(Rendered.All(waiting), one => one.Id == QueueView.LookingTableId);
        Assert.Equal(["ghost", "primary"], Variants(waiting));
    }

    [Fact]
    public void ATabThePathDoesNotNameIsTheFirstTab()
    {
        PluginView page = QueueView.Render(Queue(), "given-up");

        Assert.Contains(Rendered.All(page), one => one.Id == QueueView.LookingTableId);
        Assert.DoesNotContain(Rendered.All(page), one => one.Id == QueueView.WaitingTableId);
        Assert.Equal(["primary", "ghost"], Variants(page));
    }

    /// <remarks>
    /// The tabs are buttons in a row, the way the navigation is, so a television's remote reaches them
    /// with the D-pad and left and right move along them.
    /// </remarks>
    [Fact]
    public void TheTabsAreButtonsInARow()
    {
        PluginComponent row = Rendered.ById(QueueView.Render(Queue()), Tabs.RowId);

        Assert.Equal(Ui.RowComponent, row.Component);
        Assert.Equal(2, row.Items.Count);
        Assert.All(row.Items, button => Assert.Equal(Ui.ButtonComponent, button.Component));
        Assert.All(row.Items, button => Assert.Equal(PluginActionType.Navigate, button.Action?.Type));
    }

    /// <remarks>
    /// The stages carry no count: they are six rows, the same six on every run, and a number that never
    /// changes tells the owner nothing.
    /// </remarks>
    [Fact]
    public void TheActivityPageOfARunIsATabPerTable()
    {
        PluginView page = ActivityView.Render(Running(), new(true, null, null) { StartedAt = Now.AddMinutes(-3) });

        Assert.Equal(["Stages", "Now (1)", "Notes (2)"], Labels(page));
        Assert.Equal(["/activity/stages", "/activity/now", "/activity/notes"], Routes(page));
        Assert.Contains(Rendered.All(page), one => one.Id == ActivityView.StagesTableId);
        Assert.DoesNotContain(Rendered.All(page), one => one.Id == ActivityView.NowTableId);
        Assert.DoesNotContain(Rendered.All(page), one => one.Id == ActivityView.NotesTableId);

        PluginView notes = ActivityView.Render(
            Running(),
            new(true, null, null) { StartedAt = Now.AddMinutes(-3) },
            tab: ActivityView.NotesTableId);

        Assert.Contains(Rendered.All(notes), one => one.Id == ActivityView.NotesTableId);
        Assert.DoesNotContain(Rendered.All(notes), one => one.Id == ActivityView.StagesTableId);
        Assert.Equal(["ghost", "ghost", "primary"], Variants(notes));
    }

    /// <remarks>
    /// Only a page with two or more tables has tabs. An idle Activity page has the one table of what is in
    /// flight, and a row holding one tab is a button that goes where the reader already is.
    /// </remarks>
    [Fact]
    public void APageWithOneTableHasNoTabs()
    {
        PluginView page = ActivityView.Render(new([], [], Now), new(false, Now.AddMinutes(-1), null), tab: ActivityView.NotesTableId);

        Assert.DoesNotContain(Rendered.All(page), one => one.Id == Tabs.RowId);
        Assert.Contains(Rendered.All(page), one => one.Id == ActivityView.NowTableId);
    }

    /// <remarks>
    /// The overview's tabs are its libraries, at the library addresses that already exist, so a tab is
    /// an address that reloads to the same library and the paging under it keeps working.
    /// </remarks>
    [Fact]
    public void TheOverviewsTabsAreItsLibrariesAtTheirOwnAddresses()
    {
        PluginView page = OverviewView.Render(new(false, null, null), Libraries());

        Assert.Equal(["Series (2)", "Anime (1)"], Labels(page));
        Assert.Equal([ShowsOf(Tv, 1), ShowsOf(Anime, 1)], Routes(page));
        Assert.Contains(Rendered.All(page), one => one.Id == OverviewView.TableId(Tv));
        Assert.DoesNotContain(Rendered.All(page), one => one.Id == OverviewView.TableId(Anime));

        PluginView anime = OverviewView.Render(new(false, null, null), Libraries(), onlyLibraryId: Anime);

        Assert.Contains(Rendered.All(anime), one => one.Id == OverviewView.TableId(Anime));
        Assert.DoesNotContain(Rendered.All(anime), one => one.Id == OverviewView.TableId(Tv));
        Assert.Equal(["ghost", "primary"], Variants(anime));

        PluginView nowhere = OverviewView.Render(new(false, null, null), Libraries(), onlyLibraryId: "no-such-library");

        Assert.Contains(Rendered.All(nowhere), one => one.Id == OverviewView.TableId(Tv));
        Assert.Equal(["primary", "ghost"], Variants(nowhere));
    }

    /// <remarks>
    /// What is looked for narrows every library at once, so the count on each tab is how many of its
    /// shows match: the owner reads which library holds the show off the tabs.
    /// </remarks>
    [Fact]
    public void ASearchCountsWhatMatchesOnEachLibrarysTab()
    {
        PluginView page = OverviewView.Render(new(false, null, null), Libraries(), find: "frie");

        Assert.Equal(["Series (0)", "Anime (1)"], Labels(page));
    }

    /// <remarks>
    /// Through the plugin, which is how a page reaches anybody: the same address draws the same tab, and
    /// the page the tab belongs to is still the one the navigation marks.
    /// </remarks>
    [Fact]
    public async Task TheSameAddressDrawsTheSameTabAndTheNavigationStillMarksItsPage()
    {
        using TorrentDownloaderPlugin plugin = await Seeded();

        PluginView waiting = await View(plugin, "/queue/waiting");

        Assert.Contains(Rendered.All(waiting), one => one.Id == QueueView.WaitingTableId);
        Assert.DoesNotContain(Rendered.All(waiting), one => one.Id == QueueView.LookingTableId);
        Assert.Equal("primary", Rendered.ById(waiting, "nav-queue").Props["variant"]);

        PluginView anime = await View(plugin, ShowsOf(Anime, 1));

        Assert.Contains(Rendered.All(anime), one => one.Id == OverviewView.TableId(Anime));
        Assert.DoesNotContain(Rendered.All(anime), one => one.Id == OverviewView.TableId(Tv));
        Assert.Equal("primary", Rendered.ById(anime, "nav-overview").Props["variant"]);

        PluginView activity = await View(plugin, "/activity/notes");

        Assert.Equal("primary", Rendered.ById(activity, "nav-activity").Props["variant"]);
        Assert.Equal(
            ["nav-activity"],
            Rendered.All(activity)
                .Where(one => one.Id.StartsWith("nav-", StringComparison.Ordinal) && one.Props.GetValueOrDefault("variant") as string == "primary")
                .Select(one => one.Id));
    }

    /// <remarks>
    /// A tab's address is a page of the plugin's, declared, so the server admits it and the web app's own
    /// fetch of the address is answered — but it is not a page of its own in the navigation.
    /// </remarks>
    [Fact]
    public void ATabsAddressIsDeclaredAndIsNotAPageOfTheNavigation()
    {
        Assert.Equal(Pages.QueueTabName, Pages.Routes.Resolve("/queue/waiting")?.Route.Name);
        Assert.Equal(Pages.ActivityTabName, Pages.Routes.Resolve("/activity/notes")?.Route.Name);
        Assert.DoesNotContain(Pages.Navigable, route => route.Name is Pages.QueueTabName or Pages.ActivityTabName);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    private static IReadOnlyList<TrackedEpisode> Queue()
    {
        return
        [
            Episode(1, 1, 1, EpisodeState.Missing),
            Episode(1, 1, 2, EpisodeState.Missing),
            Episode(1, 1, 3, EpisodeState.NotAired),
        ];
    }

    private static ActivitySnapshot Running()
    {
        return new(
            [new(ActivityStage.Find, ActivityOutcome.Started, "Silo S03E06", Now.AddSeconds(-30), "asking 1337x")],
            [],
            Now)
        {
            Run = new(Now.AddMinutes(-3), new Dictionary<RunCounter, int>()),
            Notes =
            [
                new(ActivityStage.Names, "Silo S03E06", Now.AddSeconds(-20), "PreDB · 1 name"),
                new(ActivityStage.Find, "Silo S03E06", Now.AddSeconds(-5), "TorrentBay · 1 row"),
            ],
        };
    }

    private static IReadOnlyList<LibraryListing> Libraries()
    {
        return
        [
            Listing(Tv, "Series", Listed(41, "Silo", Tv), Listed(52, "Sugar", Tv)),
            Listing(Anime, "Anime", Listed(90, "Frieren", Anime)),
        ];
    }

    private static LibraryListing Listing(string id, string name, params ShowListing[] shows)
    {
        return new(new(id, name, LibraryKind.Television), new(id) { Quality = "1080p" }, shows);
    }

    private static ShowListing Listed(int id, string title, string libraryId)
    {
        ShowSettings saved = new(id);
        LibraryPreferences library = new(libraryId) { Quality = "1080p" };

        return new(
            new(id, title, 2023, libraryId, "Series", LibraryKind.Television, $"/{title}"),
            saved,
            EffectiveSettings.Of(saved, library),
            null);
    }

    private static string ShowsOf(string libraryId, int page)
    {
        return Pages.Routes.PathTo(
            Pages.LibraryShowsName,
            new Dictionary<string, string> { ["id"] = libraryId, ["page"] = page.ToString(CultureInfo.InvariantCulture) });
    }

    private static IReadOnlyList<PluginComponent> TabButtons(PluginView page)
    {
        return Rendered.ById(page, Tabs.RowId).Items;
    }

    private static IReadOnlyList<string?> Labels(PluginView page)
    {
        return [.. TabButtons(page).Select(button => button.Props.GetValueOrDefault("label") as string)];
    }

    private static IReadOnlyList<string?> Routes(PluginView page)
    {
        return [.. TabButtons(page).Select(button => button.Action?.Payload.GetValueOrDefault("route") as string)];
    }

    private static IReadOnlyList<string?> Variants(PluginView page)
    {
        return [.. TabButtons(page).Select(button => button.Props.GetValueOrDefault("variant") as string)];
    }

    private static Task<PluginView> View(TorrentDownloaderPlugin plugin, string route)
    {
        return plugin.GetViewAsync(Requests.View(route), CancellationToken.None);
    }

    private async Task<TorrentDownloaderPlugin> Seeded()
    {
        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library(Tv, "Series", "tv")
            .Library(Anime, "Anime", "anime")
            .Show(1, "Silo", Tv, year: 2023)
            .Show(2, "Frieren", Anime, year: 2023)
            .Episode(1, 1, 99, airDate: DateTime.UtcNow.AddMonths(-6), hasFile: true)
            .Episode(2, 1, 99, airDate: DateTime.UtcNow.AddMonths(-6), hasFile: true);

        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = shelves,
        });

        await (await plugin.EpisodesAsync(CancellationToken.None)).ReplaceAsync(Queue(), CancellationToken.None);

        return plugin;
    }

    private static TrackedEpisode Episode(int show, int season, int number, EpisodeState state)
    {
        return new(
            new(show, season, number),
            "Silo",
            2023,
            LibraryKind.Television,
            "An episode",
            new DateOnly(2026, 1, 1),
            state);
    }
}
