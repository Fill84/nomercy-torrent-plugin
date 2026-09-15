using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>One show or anime on the overview, with what was saved for it and what applies.</summary>
/// <param name="Show">The show, as the library holds it.</param>
/// <param name="Settings">What the owner saved for it, or a fresh record for a show nobody touched.</param>
/// <param name="Applied">What applies, with its library counted in.</param>
/// <param name="Missing">Aired episodes without a video file, or null where the plugin has not counted them.</param>
public sealed record ShowListing(Show Show, ShowSettings Settings, EffectiveSettings Applied, int? Missing);

/// <summary>One library on the overview: its preferences and every show in it.</summary>
public sealed record LibraryListing(Library Library, LibraryPreferences Preferences, IReadOnlyList<ShowListing> Shows);

/// <summary>
/// The overview page at <c>/</c>: the run's status line, then one block per library.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/show-list.md</c> § The overview page. A pure function of what it is handed, like every
/// other view, so two pages rendering the same state draw the same thing.
/// </para>
/// <para>
/// <strong>The switch is a row button, not a toggle in the row.</strong> The contract has no toggle or
/// tag field inside a table row, so a row carries two buttons: one that switches the show and one that
/// opens its settings form.
/// </para>
/// <para>
/// <strong>Paged by route, not by query.</strong> The web app sends the plugin the path of a page and
/// drops anything after a question mark, so a page number has to be part of the path:
/// <c>/libraries/:id/shows/:page</c>.
/// </para>
/// </remarks>
public static class OverviewView
{
    public const int PageSize = 50;

    /// <summary>The id of one library's table of shows.</summary>
    public static string TableId(string libraryId) => $"shows-{libraryId}";

    /// <param name="cycle">Where the run stands, for the status line.</param>
    /// <param name="libraries">Every tv and anime library, with its shows.</param>
    /// <param name="onlyLibraryId">One library's page of shows, or null for the overview of all of them.</param>
    /// <param name="page">Which page of that library's shows, counted from one.</param>
    public static PluginView Render(
        CycleStatus cycle,
        IReadOnlyList<LibraryListing> libraries,
        string? onlyLibraryId = null,
        int page = 1)
    {
        List<PluginComponent> components = [RunStatusView.Line(cycle)];

        foreach (LibraryListing library in libraries)
        {
            if (onlyLibraryId is not null && library.Library.Id != onlyLibraryId)
            {
                continue;
            }

            components.AddRange(Block(library, onlyLibraryId is null ? 1 : Math.Max(page, 1)));
        }

        return new()
        {
            Layout = PluginLayout.Wide,
            Components = [.. components],
        };
    }

    /// <summary>A library's heading, its preferences with an Edit button, its table and its paging.</summary>
    private static IEnumerable<PluginComponent> Block(LibraryListing listing, int page)
    {
        string id = listing.Library.Id;

        yield return Ui.Text($"library-{id}", listing.Library.Name, "subtitle");

        yield return Ui.Row(
            $"library-{id}-preferences",
            Ui.Text($"library-{id}-summary", Summary(listing.Preferences)),
            Ui.Button(
                $"library-{id}-edit",
                "Edit",
                Pages.Routes.GoTo(Pages.LibraryPreferencesName, new Dictionary<string, string> { ["id"] = id })));

        ShowListing[] ordered =
        [
            .. listing.Shows
                .OrderByDescending(one => one.Settings.SwitchedOn)
                .ThenBy(one => one.Show.Title, StringComparer.OrdinalIgnoreCase)
                .ThenBy(one => one.Show.Id),
        ];

        int pages = Math.Max(1, (ordered.Length + PageSize - 1) / PageSize);
        int shown = Math.Min(page, pages);

        yield return Ui.Table(
            TableId(id),
            [
                new() { Key = "show", Label = "Show" },
                new() { Key = "year", Label = "Year" },
                new() { Key = "missing", Label = "Missing" },
                new() { Key = "quality", Label = "Quality" },
                new() { Key = "codec", Label = "Codec" },
                new() { Key = "tags", Label = "Tags" },
                new() { Key = "state", Label = "State" },
                new() { Key = "controls", Label = string.Empty, Cell = PluginTableCellType.Actions },
            ],
            [.. ordered.Skip((shown - 1) * PageSize).Take(PageSize).Select(Row)],
            "This library holds no show.");

        if (pages > 1)
        {
            yield return Paging(id, shown, pages);
        }
    }

    private static PluginComponent Row(ShowListing listing)
    {
        return Ui.Row(
            $"show-{listing.Show.Id}",
            new Dictionary<string, object?>
            {
                ["show"] = listing.Show.Title,
                ["year"] = listing.Show.Year?.ToString(CultureInfo.InvariantCulture) ?? "year not known",

                // A count nobody made is not nought: the plugin counts the episodes of the shows it
                // searches for, and a show it does not search for says so.
                ["missing"] = listing.Missing?.ToString(CultureInfo.InvariantCulture) ?? "not counted",
                ["quality"] = listing.Applied.Quality ?? "not set",
                ["codec"] = listing.Applied.Codec,
                ["tags"] = Tags(listing.Applied.Wishes, listing.Applied.Musts, listing.Applied.Forbidden),
                ["state"] = State(listing.Settings),
                ["controls"] = Controls(listing),
            });
    }

    private static string State(ShowSettings settings)
    {
        return settings switch
        {
            { SwitchedOn: false } => "Off",
            { Saved: false } => "On, not saved",
            _ => "On",
        };
    }

    private static IReadOnlyList<PluginTableAction> Controls(ShowListing listing)
    {
        string show = listing.Show.Id.ToString(CultureInfo.InvariantCulture);
        bool on = listing.Settings.SwitchedOn;

        return
        [
            new()
            {
                Label = on ? "Switch off" : "Switch on",
                Action = PluginActionIntent.CallPlugin(
                    $"shows/{show}/{(on ? "off" : "on")}",
                    null,
                    PluginActionTransport.Rest),
            },
            new()
            {
                Label = "Settings",
                Action = Pages.Routes.GoTo(Pages.ShowSettingsName, new Dictionary<string, string> { ["id"] = show }),
            },
        ];
    }

    private static PluginComponent Paging(string libraryId, int page, int pages)
    {
        List<PluginComponent> buttons = [];

        if (page > 1)
        {
            buttons.Add(Ui.Button($"library-{libraryId}-previous", "Previous", PageOf(libraryId, page - 1)));
        }

        buttons.Add(Ui.Text($"library-{libraryId}-page", $"page {page} of {pages}"));

        if (page < pages)
        {
            buttons.Add(Ui.Button($"library-{libraryId}-next", "Next", PageOf(libraryId, page + 1)));
        }

        return Ui.Row($"library-{libraryId}-paging", [.. buttons]);
    }

    private static PluginActionIntent PageOf(string libraryId, int page)
    {
        return Pages.Routes.GoTo(
            Pages.LibraryShowsName,
            new Dictionary<string, string>
            {
                ["id"] = libraryId,
                ["page"] = page.ToString(CultureInfo.InvariantCulture),
            });
    }

    /// <summary>A library's preferences on one line.</summary>
    internal static string Summary(LibraryPreferences preferences)
    {
        return string.Join(
            " · ",
            preferences.Quality ?? "quality not set",
            preferences.Codec,
            preferences.Specials ? "specials on" : "specials off",
            Tags(preferences.Wishes, preferences.Musts, preferences.Forbidden));
    }

    /// <summary>The three tag lists as words, or that there are none.</summary>
    internal static string Tags(IReadOnlyList<string> wishes, IReadOnlyList<string> musts, IReadOnlyList<string> forbidden)
    {
        List<string> parts = [];

        if (wishes.Count > 0)
        {
            parts.Add($"wishes: {string.Join(", ", wishes)}");
        }

        if (musts.Count > 0)
        {
            parts.Add($"musts: {string.Join(", ", musts)}");
        }

        if (forbidden.Count > 0)
        {
            parts.Add($"forbidden: {string.Join(", ", forbidden)}");
        }

        return parts.Count == 0 ? "no tags" : string.Join(" · ", parts);
    }
}
