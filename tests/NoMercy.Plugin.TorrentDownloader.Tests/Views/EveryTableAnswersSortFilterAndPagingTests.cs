using System.Globalization;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// Every table answers <c>sort</c>, <c>order</c>, <c>filter</c>, <c>page</c> and <c>pageSize</c>.
/// </summary>
/// <remarks>
/// <para>
/// GitHub issue #5, 13 October 2026: no table of the plugin could be sorted or searched, and the client
/// side of it, media-server #593, sends these keys in the page request's query. The plugin answers them
/// already, so a client that starts sending them gets the right rows the day it does.
/// </para>
/// <para>
/// Each sort is chosen so that the order of the value and the order of the text it is drawn as disagree
/// — 9% before 10%, 900 KB/s before 2 MB/s, 900 ms before 1.5 s — so a sort on the drawn string fails
/// here, and so that the order asked for differs from the table's own.
/// </para>
/// </remarks>
public sealed class EveryTableAnswersSortFilterAndPagingTests : IDisposable
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", "table-query-" + Guid.NewGuid().ToString("n")[..8]);

    // The Queue, Looking: Andor, Severance, Silo in the order a search asks.

    [Fact]
    public void TheLookingListSortsByItsAttemptsEitherWay()
    {
        Assert.Equal(
            ["Andor S01E02", "Silo S01E01", "Severance S02E01"],
            Episodes(QueueView.Render(Looking(), query: Asked(("sort", "attempts"), ("order", "asc")))));
        Assert.Equal(
            ["Severance S02E01", "Silo S01E01", "Andor S01E02"],
            Episodes(QueueView.Render(Looking(), query: Asked(("sort", "attempts"), ("order", "desc")))));
    }

    /// <remarks>
    /// By when it was last tried, not by the words in the cell, and "never" is not a time: it is after
    /// every row that has one, whichever way the list runs.
    /// </remarks>
    [Fact]
    public void TheLookingListSortsByWhenItWasLastTriedWithNeverLast()
    {
        Assert.Equal(
            ["Severance S02E01", "Silo S01E01", "Andor S01E02"],
            Episodes(QueueView.Render(Looking(), query: Asked(("sort", "last"), ("order", "asc")))));
        Assert.Equal(
            ["Silo S01E01", "Severance S02E01", "Andor S01E02"],
            Episodes(QueueView.Render(Looking(), query: Asked(("sort", "last"), ("order", "desc")))));
    }

    /// <remarks>What is shown is what is searched: "never" is the words in Andor's Last tried cell.</remarks>
    [Fact]
    public void TheLookingListKeepsTheRowsWhoseShownTextHoldsTheFilterWhateverItsCase()
    {
        Assert.Equal(["Severance S02E01"], Episodes(QueueView.Render(Looking(), query: Asked(("filter", "SEVER")))));
        Assert.Equal(["Andor S01E02"], Episodes(QueueView.Render(Looking(), query: Asked(("filter", "never")))));
    }

    /// <remarks>
    /// A table that holds all its rows does not page: a page of one row would hide the other two with
    /// nothing on the page to reach them by.
    /// </remarks>
    [Fact]
    public void TheLookingListHoldsEveryRowWhateverPageIsAskedFor()
    {
        Assert.Equal(
            ["Severance S02E01", "Silo S01E01", "Andor S01E02"],
            Episodes(QueueView.Render(
                Looking(),
                query: Asked(("sort", "attempts"), ("order", "desc"), ("page", "2"), ("pageSize", "1")))));
    }

    /// <remarks>
    /// A query's keys are case-insensitive to ASP.NET, which is where they arrive from, so <c>?Sort=</c>
    /// is the same request as <c>?sort=</c>.
    /// </remarks>
    [Fact]
    public void TheKeysAreReadWhateverTheirCase()
    {
        Assert.Equal(
            ["Severance S02E01", "Silo S01E01", "Andor S01E02"],
            Episodes(QueueView.Render(Looking(), query: Asked(("Sort", "attempts"), ("ORDER", "desc")))));
        Assert.Equal(["Severance S02E01"], Episodes(QueueView.Render(Looking(), query: Asked(("Filter", "sever")))));
    }

    /// <remarks>
    /// An episode sorts by its show's title the way the Queue's own order does, letter by letter whatever
    /// the case, and never by the server's culture: under en-GB "Élite" sorts before "Fargo", letter by
    /// letter after it. Two servers in two countries draw the same list.
    /// </remarks>
    [Fact]
    public void AnEpisodeSortsByItsTitleAsTheQueueDoesWhateverTheServersCulture()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("en-GB");

        try
        {
            TrackedEpisode[] two =
            [
                Tracked(7, "Élite", 1, 1, EpisodeState.Missing),
                Tracked(8, "Fargo", 1, 1, EpisodeState.Missing),
            ];

            Assert.Equal(["Fargo S01E01", "Élite S01E01"], Episodes(QueueView.Render(two)));
            Assert.Equal(
                ["Fargo S01E01", "Élite S01E01"],
                Episodes(QueueView.Render(two, query: Asked(("sort", "episode"), ("order", "asc")))));
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Theory]
    [InlineData("nope", "asc")]
    [InlineData("attempts", "sideways")]
    [InlineData("controls", "asc")]
    [InlineData("", "desc")]
    public void AnUnknownSortKeyOrOrderIsTheTablesOwnOrder(string sort, string order)
    {
        Assert.Equal(
            ["Andor S01E02", "Severance S02E01", "Silo S01E01"],
            Episodes(QueueView.Render(Looking(), query: Asked(("sort", sort), ("order", order)))));
    }

    // The Queue, Waiting to air: Pluribus, Fallout, Alien by air date, the undated last.

    /// <remarks>The keys are about the table the page shows, which is the tab the path names.</remarks>
    [Fact]
    public void TheWaitingListSortsAndFiltersAsTheTabShown()
    {
        Assert.Equal(
            ["Alien S01E09", "Fallout S02E01", "Pluribus S01E05"],
            Waiting(Asked(("sort", "episode"), ("order", "asc"))));
        Assert.Equal(
            ["Fallout S02E01", "Pluribus S01E05", "Alien S01E09"],
            Waiting(Asked(("sort", "airs"), ("order", "desc"))));
        Assert.Equal(["Alien S01E09"], Waiting(Asked(("filter", "No Date"))));
        Assert.Equal(
            ["Fallout S02E01", "Pluribus S01E05", "Alien S01E09"],
            Waiting(Asked(("sort", "airs"), ("order", "desc"), ("page", "2"), ("pageSize", "1"))));
    }

    // Activity: the stages, what is in flight, and the notes.

    [Fact]
    public void TheStagesSortByTheirNameAndFilterByWhatTheyCounted()
    {
        Assert.Equal(
            ["Decide", "Download", "Find", "Grab", "Names", "Warm-up"],
            Cells(Activity(ActivityView.StagesTableId, Asked(("sort", "stage"), ("order", "asc"))), ActivityView.StagesTableId, "stage"));
        Assert.Equal(
            ["Warm-up", "Names", "Grab", "Find", "Download", "Decide"],
            Cells(Activity(ActivityView.StagesTableId, Asked(("sort", "stage"), ("order", "desc"))), ActivityView.StagesTableId, "stage"));
        Assert.Equal(
            ["Grab"],
            Cells(Activity(ActivityView.StagesTableId, Asked(("filter", "HANDED"))), ActivityView.StagesTableId, "stage"));
        Assert.Equal(
            ["Warm-up", "Names", "Grab", "Find", "Download", "Decide"],
            Cells(
                Activity(ActivityView.StagesTableId, Asked(("sort", "stage"), ("order", "desc"), ("page", "2"), ("pageSize", "2"))),
                ActivityView.StagesTableId,
                "stage"));
    }

    /// <remarks>By the stage's place in a run, not its name: Names comes before Find.</remarks>
    [Fact]
    public void WhatIsInFlightSortsByTheStagesPlaceInARun()
    {
        Assert.Equal(
            ["Andor S01E02", "Silo S03E06", "Fallout S02E01"],
            Cells(Activity(ActivityView.NowTableId, Asked(("sort", "stage"), ("order", "asc"))), ActivityView.NowTableId, "subject"));
        Assert.Equal(
            ["Fallout S02E01", "Silo S03E06", "Andor S01E02"],
            Cells(Activity(ActivityView.NowTableId, Asked(("sort", "stage"), ("order", "desc"))), ActivityView.NowTableId, "subject"));
        Assert.Equal(
            ["Andor S01E02"],
            Cells(Activity(ActivityView.NowTableId, Asked(("filter", "predb"))), ActivityView.NowTableId, "subject"));
        Assert.Equal(
            ["Fallout S02E01", "Silo S03E06", "Andor S01E02"],
            Cells(
                Activity(ActivityView.NowTableId, Asked(("sort", "stage"), ("order", "desc"), ("page", "2"), ("pageSize", "1"))),
                ActivityView.NowTableId,
                "subject"));
    }

    [Fact]
    public void TheNotesSortByTheirTimeAndTheirEpisode()
    {
        Assert.Equal(
            ["Fallout S02E01", "Andor S01E02", "Silo S03E06"],
            Cells(Activity(ActivityView.NotesTableId, Asked(("sort", "at"), ("order", "desc"))), ActivityView.NotesTableId, "episode"));
        Assert.Equal(
            ["Andor S01E02", "Fallout S02E01", "Silo S03E06"],
            Cells(Activity(ActivityView.NotesTableId, Asked(("sort", "episode"), ("order", "asc"))), ActivityView.NotesTableId, "episode"));
        Assert.Equal(
            ["Andor S01E02"],
            Cells(Activity(ActivityView.NotesTableId, Asked(("filter", "torrentbay"))), ActivityView.NotesTableId, "episode"));
        Assert.Equal(
            ["Fallout S02E01", "Andor S01E02", "Silo S03E06"],
            Cells(
                Activity(ActivityView.NotesTableId, Asked(("sort", "at"), ("order", "desc"), ("page", "2"), ("pageSize", "1"))),
                ActivityView.NotesTableId,
                "episode"));
    }

    // Downloads: Silo at 10% and 2 MB/s, Fallout not started, Andor at 9% and 900 KB/s.

    /// <remarks>
    /// By how much is done, which is not the order of the words: "10% of 1 GB" is before "9% of 1 GB" as
    /// text. A download with no size has no progress, and is last either way.
    /// </remarks>
    [Fact]
    public void DownloadsSortByHowMuchIsDone()
    {
        Assert.Equal(["Andor", "Silo", "Fallout"], Releases(DownloadsView.Render(Downloads(), Asked(("sort", "progress"), ("order", "asc")))));
        Assert.Equal(["Silo", "Andor", "Fallout"], Releases(DownloadsView.Render(Downloads(), Asked(("sort", "progress"), ("order", "desc")))));
    }

    [Fact]
    public void DownloadsSortByTheRateInBytes()
    {
        Assert.Equal(["Andor", "Silo", "Fallout"], Releases(DownloadsView.Render(Downloads(), Asked(("sort", "rate"), ("order", "asc")))));
        Assert.Equal(["Silo", "Andor", "Fallout"], Releases(DownloadsView.Render(Downloads(), Asked(("sort", "rate"), ("order", "desc")))));
    }

    [Fact]
    public void DownloadsFilterByTheWordsTheRowShows()
    {
        Assert.Equal(["Fallout"], Releases(DownloadsView.Render(Downloads(), Asked(("filter", "NOT STARTED")))));
        Assert.Equal(
            ["Silo", "Andor", "Fallout"],
            Releases(DownloadsView.Render(Downloads(), Asked(("sort", "rate"), ("order", "desc"), ("page", "2"), ("pageSize", "1")))));
    }

    // History: Silo grabbed three hours ago, Andor failed an hour ago, Fallout decided two hours ago.

    [Fact]
    public void HistorySortsByWhenAndByWhyWithNoReasonLast()
    {
        Assert.Equal(["Silo S03E06", "Fallout S02E01", "Andor S01E02"], Subjects(Asked(("sort", "at"), ("order", "asc"))));
        Assert.Equal(["Andor S01E02", "Fallout S02E01", "Silo S03E06"], Subjects(Asked(("sort", "at"), ("order", "desc"))));
        Assert.Equal(["Andor S01E02", "Silo S03E06", "Fallout S02E01"], Subjects(Asked(("sort", "detail"), ("order", "desc"))));
    }

    [Fact]
    public void HistoryFiltersByTheWordsTheRowShows()
    {
        Assert.Equal(["Andor S01E02"], Subjects(Asked(("filter", "VIDEO"))));
        Assert.Equal(
            ["Andor S01E02", "Fallout S02E01", "Silo S03E06"],
            Subjects(Asked(("sort", "at"), ("order", "desc"), ("page", "2"), ("pageSize", "1"))));
    }

    // Sources: EZTV took 900 ms, Nyaa was never asked, 1337x took 1.5 s.

    /// <remarks>By how long it took: "1.5 s" is before "900 ms" as text and after it as a time.</remarks>
    [Fact]
    public void SourcesSortByHowLongTheyTook()
    {
        Assert.Equal(["EZTV", "1337x", "Nyaa"], Names(SourcesView.Render(Sources(), Now, query: Asked(("sort", "took"), ("order", "asc")))));
        Assert.Equal(["1337x", "EZTV", "Nyaa"], Names(SourcesView.Render(Sources(), Now, query: Asked(("sort", "took"), ("order", "desc")))));
    }

    [Fact]
    public void SourcesSortByTheirRowsAndFilterByWhatIsShown()
    {
        Assert.Equal(["EZTV", "1337x", "Nyaa"], Names(SourcesView.Render(Sources(), Now, query: Asked(("sort", "rows"), ("order", "asc")))));
        Assert.Equal(["Nyaa"], Names(SourcesView.Render(Sources(), Now, query: Asked(("filter", "NEVER")))));
        Assert.Equal(
            ["1337x", "EZTV", "Nyaa"],
            Names(SourcesView.Render(Sources(), Now, query: Asked(("sort", "took"), ("order", "desc"), ("page", "2"), ("pageSize", "1")))));
    }

    // The overview: Andor, Fallout, Pluribus, Severance, Silo, all switched off, alphabetical.

    [Fact]
    public void TheOverviewSortsByTitleAndByWhatIsMissingWithNotCountedLast()
    {
        Assert.Equal(
            ["Silo", "Severance", "Pluribus", "Fallout", "Andor"],
            Shows(Overview(Asked(("sort", "show"), ("order", "desc")))));
        Assert.Equal(
            ["Fallout", "Silo", "Andor", "Severance", "Pluribus"],
            Shows(Overview(Asked(("sort", "missing"), ("order", "asc")))));
        Assert.Equal(
            ["Severance", "Andor", "Silo", "Fallout", "Pluribus"],
            Shows(Overview(Asked(("sort", "missing"), ("order", "desc")))));
    }

    [Fact]
    public void TheOverviewFiltersByTheWordsARowShowsAndSaysHowManyMatch()
    {
        PluginView page = Overview(Asked(("filter", "2022")));

        Assert.Equal(["Andor", "Severance"], Shows(page));
        Assert.Equal("page 1 of 1 · 2 shows", PagingLine(page));
    }

    /// <remarks>
    /// Sorting runs before paging, so the second page carries on where the first stopped: three pages of
    /// two, read one after another, are the whole list in the order asked for.
    /// </remarks>
    [Fact]
    public void TheOverviewsSecondPageAfterASortContinuesTheFirstsOrder()
    {
        IReadOnlyList<string?> first = Shows(Overview(Asked(("sort", "show"), ("order", "desc"), ("page", "1"), ("pageSize", "2"))));
        PluginView second = Overview(Asked(("sort", "show"), ("order", "desc"), ("page", "2"), ("pageSize", "2")));
        IReadOnlyList<string?> third = Shows(Overview(Asked(("sort", "show"), ("order", "desc"), ("page", "3"), ("pageSize", "2"))));

        Assert.Equal(["Silo", "Severance"], first);
        Assert.Equal(["Pluribus", "Fallout"], Shows(second));
        Assert.Equal(["Andor"], third);
        Assert.Equal("page 2 of 3 · 5 shows", PagingLine(second));
    }

    /// <remarks>
    /// Decided for S14-07: Previous and Next go to the page in the path, fifty
    /// to a page and with no query on it, so under a query of ten to a page the Next of page 2 drew shows
    /// 101 to 150 and dropped the sort and the filter. A client paging by query pages by query; the page
    /// says where it is and how many match, and offers no button that would take it somewhere else.
    /// </remarks>
    [Fact]
    public void UnderAQueryTheOverviewDrawsNoPreviousOrNext()
    {
        PluginView page = Overview(Asked(("page", "2"), ("pageSize", "2")));

        Assert.Equal("page 2 of 3 · 5 shows", PagingLine(page));
        Assert.DoesNotContain(
            Rendered.All(page),
            one => one.Id == $"library-{Tv}-previous" || one.Id == $"library-{Tv}-next");
    }

    [Fact]
    public void APagePastTheEndIsTheLastPage()
    {
        PluginView page = Overview(Asked(("sort", "show"), ("order", "desc"), ("page", "9"), ("pageSize", "2")));

        Assert.Equal(["Andor"], Shows(page));
        Assert.Equal("page 3 of 3 · 5 shows", PagingLine(page));
    }

    /// <remarks>
    /// The page in the path is the overview's own paging, which Previous and Next use; a page in the query
    /// is what a client asked for, and wins.
    /// </remarks>
    [Fact]
    public void AQueryPageWinsOverThePathsPage()
    {
        PluginView page = OverviewView.Render(
            new(false, null, null),
            [Library(Shelf())],
            onlyLibraryId: Tv,
            page: 3,
            query: Asked(("page", "1"), ("pageSize", "2")));

        Assert.Equal(["Andor", "Fallout"], Shows(page));
    }

    /// <remarks>
    /// Decided for S14-07: a page holds at most what the table holds by itself, fifty
    /// shows, so no client can have the plugin draw a library of thousands at once. A size out of range,
    /// or not a number, is the table's own.
    /// </remarks>
    [Theory]
    [InlineData("10", 10)]
    [InlineData("50", 50)]
    [InlineData("51", 50)]
    [InlineData("100000", 50)]
    [InlineData("0", 50)]
    [InlineData("-3", 50)]
    [InlineData("ten", 50)]
    public void APageHoldsAtMostTheTablesOwnPageSize(string asked, int held)
    {
        PluginView page = OverviewView.Render(
            new(false, null, null),
            [Library([.. Enumerable.Range(1, 60).Select(id => Listed(id, $"Show {id:000}", 2020, null))])],
            query: Asked(("pageSize", asked)));

        Assert.Equal(held, Shows(page).Count);
    }

    /// <remarks>
    /// A client that sends nothing sees the page exactly as it was before any of this: the plugin's own
    /// order, fifty to a page, and the paging line in its own words.
    /// </remarks>
    [Fact]
    public void WithoutAQueryTheOverviewIsAsItWas()
    {
        PluginView page = OverviewView.Render(
            new(false, null, null),
            [Library([.. Enumerable.Range(1, 60).Select(id => Listed(id, $"Show {id:000}", 2020, null))])]);

        Assert.Equal(50, Shows(page).Count);
        Assert.Equal("Show 001", Shows(page)[0]);
        Assert.Equal("page 1 of 2", PagingLine(page));
        Assert.Equal(PluginActionType.Navigate, Rendered.ById(page, $"library-{Tv}-next").Action?.Type);
        Assert.DoesNotContain(Rendered.All(page), one => one.Id == $"library-{Tv}-previous");
        Assert.DoesNotContain(Rendered.All(Overview(TableQuery.None)), one => one.Id == $"library-{Tv}-paging");
    }

    /// <remarks>Rows that exist and do not match are not an empty table, and the message says which.</remarks>
    [Fact]
    public void AFilterThatMatchesNothingSaysSoRatherThanThatTheTableIsEmpty()
    {
        PluginComponent table = Rendered.ById(QueueView.Render(Looking(), query: Asked(("filter", "Battlestar"))), QueueView.LookingTableId);

        Assert.Empty(table.Items);
        Assert.Equal("Nothing here matches 'Battlestar'.", table.Props["emptyMessage"]);
    }

    /// <remarks>
    /// A row's buttons are not what it says: "Search now" is on every row of the Looking list, and a
    /// filter matched against it would keep them all.
    /// </remarks>
    [Fact]
    public void AButtonsLabelIsNotTextAFilterMatches()
    {
        Assert.Empty(Episodes(QueueView.Render(Looking(), query: Asked(("filter", "Search now")))));
    }

    // The request: the server hands the query over, and so does the plugin's own address for a page.

    [Fact]
    public async Task AQueryOnTheRequestReachesTheTable()
    {
        using TorrentDownloaderPlugin plugin = await Seeded();

        PluginViewRequest request = Requests.View("/queue");
        PluginView page = await plugin.GetViewAsync(
            new()
            {
                Route = request.Route,
                Caller = request.Caller,
                Query = new() { ["filter"] = "S01E02" },
            },
            CancellationToken.None);

        Assert.Equal(["Andor S01E02"], Episodes(page));
    }

    /// <remarks>
    /// The address the web app fetches a page by is answered by the plugin's own controller, which built
    /// its request from the route alone, so a query on it never reached a table.
    /// </remarks>
    [Theory]
    [InlineData("?filter=S01E02")]
    [InlineData("?Filter=S01E02")]
    public async Task AQueryOnThePagesOwnAddressReachesTheTable(string asked)
    {
        using TorrentDownloaderPlugin plugin = await Seeded();

        PagesController controller = new PagesController(new LoadedPlugins(plugin)).On(plugin.Id);
        controller.HttpContext.Request.QueryString = new QueryString(asked);

        OkObjectResult result = Assert.IsType<OkObjectResult>(await controller.Page("queue", CancellationToken.None));
        PluginView page = Assert.IsType<PluginDataResponse<PluginView>>(result.Value).Data!;

        Assert.Equal(["Andor S01E02"], Episodes(page));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    private static TableQuery Asked(params (string Key, string Value)[] keys)
    {
        return TableQuery.From(keys.ToDictionary(key => key.Key, key => key.Value));
    }

    private static IReadOnlyList<string?> Cells(PluginView page, string tableId, string key)
    {
        return [.. Rendered.ById(page, tableId).Items.Select(row => Convert.ToString(row.Props[key], CultureInfo.InvariantCulture))];
    }

    private static IReadOnlyList<string?> Episodes(PluginView page)
    {
        return Cells(page, QueueView.LookingTableId, "episode");
    }

    private static IReadOnlyList<string?> Waiting(TableQuery query)
    {
        return Cells(QueueView.Render(Looking(), QueueView.WaitingTableId, query), QueueView.WaitingTableId, "episode");
    }

    private static IReadOnlyList<TrackedEpisode> Looking()
    {
        return
        [
            Tracked(1, "Silo", 1, 1, EpisodeState.Missing, attempts: 3, last: Now.AddHours(-1)),
            Tracked(2, "Andor", 1, 2, EpisodeState.Missing, attempts: 1, last: null),
            Tracked(3, "Severance", 2, 1, EpisodeState.Missing, attempts: 5, last: Now.AddDays(-2)),
            Tracked(4, "Pluribus", 1, 5, EpisodeState.NotAired, airs: new DateOnly(2026, 11, 1)),
            Tracked(5, "Fallout", 2, 1, EpisodeState.NotAired, airs: new DateOnly(2026, 12, 17)),
            Tracked(6, "Alien", 1, 9, EpisodeState.NotAired),
        ];
    }

    private static TrackedEpisode Tracked(
        int show,
        string title,
        int season,
        int number,
        EpisodeState state,
        int attempts = 0,
        DateTimeOffset? last = null,
        DateOnly? airs = null)
    {
        return new(new(show, season, number), title, 2023, LibraryKind.Television, "An episode", airs, state, Attempts: attempts, LastSearchAt: last);
    }

    private static PluginView Activity(string tab, TableQuery query)
    {
        ActivitySnapshot running = new(
            [
                new(ActivityStage.Find, ActivityOutcome.Started, "Silo S03E06", Now.AddSeconds(-30), "asking 1337x"),
                new(ActivityStage.Names, ActivityOutcome.Started, "Andor S01E02", Now.AddSeconds(-20), "asking PreDB"),
                new(ActivityStage.Grab, ActivityOutcome.Started, "Fallout S02E01", Now.AddSeconds(-10)),
            ],
            [],
            Now)
        {
            Run = new(Now.AddMinutes(-3), new Dictionary<RunCounter, int> { [RunCounter.Taken] = 1 }),
            Notes =
            [
                new(ActivityStage.Names, "Silo S03E06", Now.AddSeconds(-40), "PreDB · 1 name"),
                new(ActivityStage.Find, "Andor S01E02", Now.AddSeconds(-20), "TorrentBay · 1 row"),
                new(ActivityStage.Decide, "Fallout S02E01", Now.AddSeconds(-5), "decided"),
            ],
        };

        return ActivityView.Render(running, new(true, null, null) { StartedAt = Now.AddMinutes(-3) }, [], tab, query);
    }

    private static IReadOnlyList<DownloadRow> Downloads()
    {
        const long gigabyte = 1024L * 1024 * 1024;

        return
        [
            Download("Silo", gigabyte / 10, gigabyte, 2 * 1024 * 1024),
            new(new("fallout", "magnet:?xt=urn:btih:fallout", "Fallout", GrabState.Grabbed), null),
            Download("Andor", gigabyte * 9 / 100, gigabyte, 900 * 1024),
        ];
    }

    private static DownloadRow Download(string release, long done, long total, double rate)
    {
        string hash = release.ToLowerInvariant();

        return new(
            new(hash, $"magnet:?xt=urn:btih:{hash}", release, GrabState.Downloading),
            new(hash, release, TorrentState.Downloading, done, total, rate, 0, 4, 2, 0.1, TimeSpan.FromMinutes(5), null));
    }

    private static IReadOnlyList<string?> Releases(PluginView page)
    {
        return Cells(page, DownloadsView.TableId, "release");
    }

    private static IReadOnlyList<string?> Subjects(TableQuery query)
    {
        PluginView page = HistoryView.Render(
            [
                new("grabbed", Now.AddHours(-3), "Silo S03E06", "1080p WEB"),
                new("failed", Now.AddHours(-1), "Andor S01E02", "no video file in it"),
                new("decided", Now.AddHours(-2), "Fallout S02E01", null),
            ],
            query);

        return Cells(page, HistoryView.TableId, "subject");
    }

    private static IReadOnlyList<SourceReport> Sources()
    {
        return
        [
            new("EZTV", Now.AddMinutes(-4), 3, null, TimeSpan.FromMilliseconds(900), null),
            new("Nyaa", null, 0, null, TimeSpan.Zero, null),
            new("1337x", Now.AddMinutes(-5), 12, null, TimeSpan.FromSeconds(1.5), null),
        ];
    }

    private static IReadOnlyList<string?> Names(PluginView page)
    {
        return Cells(page, SourcesView.TableId, "source");
    }

    private static PluginView Overview(TableQuery query)
    {
        return OverviewView.Render(new(false, null, null), [Library(Shelf())], query: query);
    }

    private static ShowListing[] Shelf()
    {
        return
        [
            Listed(1, "Silo", 2023, 1),
            Listed(2, "Andor", 2022, 3),
            Listed(3, "Pluribus", 2025, null),
            Listed(4, "Severance", 2022, 7),
            Listed(5, "Fallout", 2024, 0),
        ];
    }

    private static LibraryListing Library(ShowListing[] shows)
    {
        return new(new(Tv, "Series", LibraryKind.Television), new(Tv) { Quality = "1080p" }, shows);
    }

    private static ShowListing Listed(int id, string title, int year, int? missing)
    {
        ShowSettings saved = new(id);
        LibraryPreferences library = new(Tv) { Quality = "1080p" };

        return new(
            new(id, title, year, Tv, "Series", LibraryKind.Television, $"/{title}"),
            saved,
            EffectiveSettings.Of(saved, library),
            missing);
    }

    private static IReadOnlyList<string?> Shows(PluginView page)
    {
        return Cells(page, OverviewView.TableId(Tv), "show");
    }

    private static string? PagingLine(PluginView page)
    {
        return Rendered.ById(page, $"library-{Tv}-page").Props["value"] as string;
    }

    private async Task<TorrentDownloaderPlugin> Seeded()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new FakeLibraryQuery().Library(Tv, "Series", "tv"),
        });

        await (await plugin.EpisodesAsync(CancellationToken.None)).ReplaceAsync(Looking(), CancellationToken.None);

        return plugin;
    }
}
