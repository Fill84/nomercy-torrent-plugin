using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The overview page at <c>/</c>: every show and anime, one list per library, each switched on one by
/// one. <c>docs/specs/show-list.md</c> § The overview page.
/// </summary>
public class OverviewViewTests
{
    private const string Tv = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string Anime = "01HQ5W4GAVF30N10RT6XCF6AJQ";

    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void EveryShowOfEveryTvAndAnimeLibraryIsListedPerLibrary()
    {
        PluginView page = OverviewView.Render(
            Idle(),
            [
                Listing(Tv, "Series", LibraryKind.Television, Listed(41, "Silo", Tv), Listed(52, "Sugar", Tv)),
                Listing(Anime, "Anime", LibraryKind.Anime, Listed(90, "Frieren", Anime)),
            ]);

        Assert.Equal(["Silo", "Sugar"], Titles(page, Tv));
        Assert.Equal(["Frieren"], Titles(page, Anime));
    }

    /// <remarks>
    /// On or off, and nothing between: a show switched on follows its library until the owner sets
    /// something on the show itself (the owner's rule of 16 September 2026), so there is no "On, not saved".
    /// </remarks>
    [Fact]
    public void ARowSaysOnOrOff()
    {
        PluginView page = OverviewView.Render(
            Idle(),
            [
                Listing(
                    Tv,
                    "Series",
                    LibraryKind.Television,
                    Listed(41, "Alpha", Tv, new(41) { SwitchedOn = true }),
                    Listed(52, "Bravo", Tv, new(52) { SwitchedOn = true }),
                    Listed(63, "Charlie", Tv, new(63))),
            ]);

        Assert.Equal("On", Cell(page, Tv, "Alpha", "state"));
        Assert.Equal("On", Cell(page, Tv, "Bravo", "state"));
        Assert.Equal("Off", Cell(page, Tv, "Charlie", "state"));
    }

    [Fact]
    public void SwitchedOnShowsComeFirstAndEachGroupIsAlphabetical()
    {
        PluginView page = OverviewView.Render(
            Idle(),
            [
                Listing(
                    Tv,
                    "Series",
                    LibraryKind.Television,
                    Listed(1, "Zulu", Tv),
                    Listed(2, "Yankee", Tv, new(2) { SwitchedOn = true }),
                    Listed(3, "alpha", Tv),
                    Listed(4, "Bravo", Tv, new(4) { SwitchedOn = true })),
            ]);

        Assert.Equal(["Bravo", "Yankee", "alpha", "Zulu"], Titles(page, Tv));
    }

    [Fact]
    public void ATablePagesAtFiftyRows()
    {
        ShowListing[] shows = [.. Enumerable.Range(1, 120).Select(id => Listed(id, $"Show {id:000}", Tv))];

        PluginView first = OverviewView.Render(Idle(), [Listing(Tv, "Series", LibraryKind.Television, shows)]);

        Assert.Equal(OverviewView.PageSize, Titles(first, Tv).Count);
        Assert.Equal("Show 001", Titles(first, Tv)[0]);
        Assert.Contains(
            Destinations(first),
            route => route == Pages.Routes.PathTo(Pages.LibraryShowsName, Parameters(Tv, 2)));

        PluginView third = OverviewView.Render(
            Idle(),
            [Listing(Tv, "Series", LibraryKind.Television, shows)],
            onlyLibraryId: Tv,
            page: 3);

        Assert.Equal(20, Titles(third, Tv).Count);
        Assert.Equal("Show 101", Titles(third, Tv)[0]);
        Assert.Contains(
            Destinations(third),
            route => route == Pages.Routes.PathTo(Pages.LibraryShowsName, Parameters(Tv, 2)));
        Assert.DoesNotContain(
            Destinations(third),
            route => route == Pages.Routes.PathTo(Pages.LibraryShowsName, Parameters(Tv, 4)));
    }

    [Fact]
    public void EveryRowCarriesASwitchAndASettingsButton()
    {
        PluginView page = OverviewView.Render(
            Idle(),
            [
                Listing(
                    Tv,
                    "Series",
                    LibraryKind.Television,
                    Listed(41, "Silo", Tv, new(41) { SwitchedOn = true }),
                    Listed(52, "Sugar", Tv)),
            ]);

        IReadOnlyList<PluginTableAction> on = Controls(page, Tv, "Silo");

        Assert.Equal("Switch off", on[0].Label);
        Assert.Equal("shows/41/off", on[0].Action!.Payload["method"]);
        Assert.Equal("Settings", on[1].Label);
        Assert.Equal(PluginActionType.Navigate, on[1].Action!.Type);
        Assert.Equal(Pages.Routes.PathTo(Pages.ShowSettingsName, new Dictionary<string, string> { ["id"] = "41" }), on[1].Action!.Payload["route"]);

        IReadOnlyList<PluginTableAction> off = Controls(page, Tv, "Sugar");

        Assert.Equal("Switch on", off[0].Label);
        Assert.Equal("shows/52/on", off[0].Action!.Payload["method"]);
    }

    [Fact]
    public void ARowSaysWhatAppliesAndHowManyAreMissing()
    {
        LibraryPreferences series = new(Tv) { Quality = "1080p", Codec = "h264", Forbidden = ["HDR"] };
        ShowSettings silo = new(41) { SwitchedOn = true, Wishes = ["NTb"] };

        PluginView page = OverviewView.Render(
            Idle(),
            [new(new(Tv, "Series", LibraryKind.Television), series, [new(Show(41, "Silo", Tv), silo, EffectiveSettings.Of(silo, series), 3)])]);

        Assert.Equal("1080p", Cell(page, Tv, "Silo", "quality"));
        Assert.Equal("h264", Cell(page, Tv, "Silo", "codec"));
        Assert.Equal("3", Cell(page, Tv, "Silo", "missing"));
        Assert.Contains("NTb", Cell(page, Tv, "Silo", "tags"), StringComparison.Ordinal);
        Assert.Contains("HDR", Cell(page, Tv, "Silo", "tags"), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A count nobody made is not nought. A show that is off is not counted, and says so.
    /// </remarks>
    [Fact]
    public void AMissingCountNobodyMadeSaysSoAndIsNotNought()
    {
        PluginView page = OverviewView.Render(Idle(), [Listing(Tv, "Series", LibraryKind.Television, Listed(41, "Silo", Tv))]);

        Assert.NotEqual("0", Cell(page, Tv, "Silo", "missing"));
        Assert.False(string.IsNullOrWhiteSpace(Cell(page, Tv, "Silo", "missing")));
    }

    [Fact]
    public void EachLibraryShowsItsPreferencesWithAnEditButton()
    {
        LibraryPreferences series = new(Tv) { Quality = "720p", Codec = "h265", Wishes = ["WEB"] };

        PluginView page = OverviewView.Render(
            Idle(),
            [new(new(Tv, "Series", LibraryKind.Television), series, [])]);

        string words = string.Join(" ", Rendered.Words(page));

        Assert.Contains("720p", words, StringComparison.Ordinal);
        Assert.Contains("h265", words, StringComparison.Ordinal);
        Assert.Contains("WEB", words, StringComparison.Ordinal);
        Assert.Contains(
            Destinations(page),
            route => route == Pages.Routes.PathTo(Pages.LibraryPreferencesName, new Dictionary<string, string> { ["id"] = Tv }));
    }

    [Fact]
    public void ThePageOpensWithTheRunsStatusLine()
    {
        PluginView page = OverviewView.Render(
            new(false, Now.AddMinutes(-14), Now.AddHours(1)),
            [Listing(Tv, "Series", LibraryKind.Television)]);

        string words = string.Join(" ", Rendered.Words(page));

        Assert.Contains(Now.AddMinutes(-14).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), words, StringComparison.Ordinal);
        Assert.Contains(Now.AddHours(1).ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture), words, StringComparison.Ordinal);
        Assert.Equal(Rendered.ById(page, "status").Id, page.Components![0].Id);
        Assert.Contains(
            Rendered.All(page),
            one => one.Action?.Payload.GetValueOrDefault("method") as string == SettingsView.RunAction);
    }

    private static CycleStatus Idle()
    {
        return new(false, null, null);
    }

    private static LibraryListing Listing(string id, string name, LibraryKind kind, params ShowListing[] shows)
    {
        return new(new(id, name, kind), new(id) { Quality = "1080p" }, shows);
    }

    private static ShowListing Listed(int id, string title, string libraryId, ShowSettings? settings = null)
    {
        ShowSettings saved = settings ?? new ShowSettings(id);
        LibraryPreferences library = new(libraryId) { Quality = "1080p" };

        return new(Show(id, title, libraryId), saved, EffectiveSettings.Of(saved, library), null);
    }

    private static Show Show(int id, string title, string libraryId)
    {
        return new(id, title, 2023, libraryId, "Series", LibraryKind.Television, $"/{title}");
    }

    private static IReadOnlyDictionary<string, string> Parameters(string libraryId, int page)
    {
        return new Dictionary<string, string> { ["id"] = libraryId, ["page"] = page.ToString(CultureInfo.InvariantCulture) };
    }

    private static PluginComponent Table(PluginView page, string libraryId)
    {
        return Rendered.ById(page, OverviewView.TableId(libraryId));
    }

    private static IReadOnlyList<string> Titles(PluginView page, string libraryId)
    {
        return [.. Table(page, libraryId).Items.Select(row => (string)row.Props["show"]!)];
    }

    private static PluginComponent RowOf(PluginView page, string libraryId, string title)
    {
        return Table(page, libraryId).Items.Single(row => (string?)row.Props["show"] == title);
    }

    private static string Cell(PluginView page, string libraryId, string title, string key)
    {
        return Assert.IsType<string>(RowOf(page, libraryId, title).Props[key]);
    }

    private static IReadOnlyList<PluginTableAction> Controls(PluginView page, string libraryId, string title)
    {
        return Assert.IsAssignableFrom<IReadOnlyList<PluginTableAction>>(RowOf(page, libraryId, title).Props["controls"]);
    }

    private static IEnumerable<string> Destinations(PluginView page)
    {
        return Rendered.All(page)
            .Select(one => one.Action)
            .OfType<PluginActionIntent>()
            .Where(action => action.Type == PluginActionType.Navigate)
            .Select(action => action.Payload.GetValueOrDefault("route"))
            .OfType<string>();
    }
}
