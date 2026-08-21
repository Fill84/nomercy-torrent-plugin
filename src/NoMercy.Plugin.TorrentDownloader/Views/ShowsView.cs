using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Every show with anything outstanding, and how much.
/// </summary>
/// <remarks>
/// A pure function of the summaries it is handed. The counts are counted from
/// the rows, so the number at the top of a row and the list it summarises
/// cannot disagree.
/// </remarks>
public static class ShowsView
{
    public const string TableId = "shows";

    public static PluginView Render(IReadOnlyList<ShowSummary> shows)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
            [
                Ui.Table(
                    TableId,
                    [
                        new() { Key = "show", Label = "Show" },
                        new() { Key = "type", Label = "Type" },
                        new() { Key = "missing", Label = "Missing" },
                        new() { Key = "waiting", Label = "Waiting to air" },
                        new() { Key = "givenup", Label = "Given up for now" },
                    ],
                    [
                        .. shows.Select(show => Ui.Row(
                            $"{TableId}-{show.ShowId}",
                            new Dictionary<string, object?>
                            {
                                ["show"] = show.Year is null ? show.Title : $"{show.Title} ({show.Year})",
                                // The server's own classification, said plainly.
                                // It is also which library the episode goes back
                                // to, so it is worth a column of its own.
                                ["type"] = show.Kind == LibraryKind.Anime ? "anime" : "tv",
                                ["missing"] = show.Missing,
                                ["waiting"] = show.WaitingToAir,
                                ["givenup"] = show.GivenUpForNow,
                            })),
                    ],
                    // Not an EmptyState: nothing outstanding means every episode
                    // of every show is on disk, which is the plugin working.
                    "Nothing is outstanding. Every episode of every show is on disk."),
            ],
        };
    }
}
