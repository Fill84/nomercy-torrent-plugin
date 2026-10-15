using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What is being looked for, and what has not aired yet — two lists, never one.
/// </summary>
/// <remarks>
/// Separated because they answer different questions and mixing them makes both
/// answers wrong: an unaired episode counted among the missing is work the
/// plugin is not doing. There used to be a third list, <em>given up for now</em>
/// — the owner's decision of 12 September 2026 dropped the state behind it
/// outright, so there is nothing left for a third list to hold: an episode
/// stays in Looking however many times it has been searched, with its attempts
/// and when it was last tried carried on the row, so a hopeless one is still
/// visible as one rather than gone from the page.
/// </remarks>
public static class QueueView
{
    public const string LookingTableId = "looking";

    /// <summary>The action that looks for one episode straight away.</summary>
    // A control's "method" is the path the client posts to:
    // plugins/{id}/{method}, straight through. Naming the action instead of the
    // route gave every button on every page a URL this plugin does not serve,
    // and nothing anyone pressed did anything at all.
    public const string SearchAction = "queue/search";
    public const string WaitingTableId = "waiting";

    /// <param name="tracked">Every episode the plugin tracks.</param>
    /// <param name="tab">The list the path names, or null for the first.</param>
    /// <remarks>
    /// One list at a time, under a tab each (GitHub issue #4): stacked, Waiting to air began wherever
    /// Looking happened to end. The tab's label is the list's name, so there is no heading above it.
    /// </remarks>
    public static PluginView Render(IReadOnlyList<TrackedEpisode> tracked, string? tab = null)
    {
        IReadOnlyList<TrackedEpisode> looking = QueueOrder.Order(tracked);
        TrackedEpisode[] waiting = [.. tracked.Where(episode => episode.State == EpisodeState.NotAired)];

        return new()
        {
            Layout = PluginLayout.Wide,
            Components =
            [
                .. Tabs.Draw(
                    [
                        new(LookingTableId, "Looking", looking.Count, TabRoute(LookingTableId), [Looking(looking)]),
                        new(WaitingTableId, "Waiting to air", waiting.Length, TabRoute(WaitingTableId), [Waiting(waiting)]),
                    ],
                    tab),
            ],
        };
    }

    private static string TabRoute(string tab)
    {
        return Pages.Routes.PathTo(Pages.QueueTabName, new Dictionary<string, string> { ["tab"] = tab });
    }

    /// <remarks>
    /// In the order a search will actually ask, from the same rule it
    /// uses — a page that showed a different order would be a guess about what
    /// the plugin is about to do.
    /// </remarks>
    private static PluginComponent Looking(IReadOnlyList<TrackedEpisode> ordered)
    {
        return Ui.Table(
            LookingTableId,
            [
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "attempts", Label = "Attempts" },
                new() { Key = "last", Label = "Last tried" },
                new() { Key = "controls", Label = string.Empty, Cell = PluginTableCellType.Actions },
            ],
            [
                .. ordered.Select(episode => Ui.Row(
                    $"{LookingTableId}-{Id(episode)}",
                    new Dictionary<string, object?>
                    {
                        ["episode"] = Name(episode),
                        ["attempts"] = episode.Attempts,
                        // Never searched is not the same as searched long ago,
                        // and nought would be a date.
                        ["last"] = episode.LastSearchAt?.ToString("u") ?? "never",

                        // A button in the row, where a click on the whole row
                        // used to do it with nothing on the page to say so
                        // (docs/specs/pages.md § Queue). Looking for one episode
                        // now is a decision about that row, and the attempts
                        // beside it are what an owner reads before taking it.
                        ["controls"] = new PluginTableAction[] { new() { Label = "Search now", Action = Look(episode) } },
                    })),
            ],
            "Nothing is being looked for.");
    }

    /// <summary>Looking for one episode now, outside the cadence.</summary>
    /// <remarks>
    /// The button an owner presses when something has just aired and they do
    /// not want to wait six hours for the next pass.
    /// </remarks>
    private static PluginActionIntent Look(TrackedEpisode episode)
    {
        return PluginActionIntent.CallPlugin(
            SearchAction,
            new Dictionary<string, object?>
            {
                ["showId"] = episode.Key.ShowId,
                ["season"] = episode.Key.Season,
                ["episode"] = episode.Key.Number,
            },
            PluginActionTransport.Rest);
    }

    private static PluginComponent Waiting(IEnumerable<TrackedEpisode> episodes)
    {
        return Ui.Table(
            WaitingTableId,
            [
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "airs", Label = "Airs" },
            ],
            [
                .. episodes
                    .OrderBy(episode => episode.AirDate ?? DateOnly.MaxValue)
                    .ThenBy(episode => episode.Key.ShowId)
                    .ThenBy(episode => episode.Key.Season)
                    .ThenBy(episode => episode.Key.Number)
                    .Select(episode => Ui.Row(
                        $"{WaitingTableId}-{Id(episode)}",
                        new Dictionary<string, object?>
                        {
                            ["episode"] = Name(episode),
                            // An episode with no announced date says so. A
                            // blank would read as a date nobody had filled in.
                            ["airs"] = episode.AirDate?.ToString("yyyy-MM-dd") ?? "no date announced",
                        })),
            ],
            "Nothing is waiting to air.");
    }

    private static string Name(TrackedEpisode episode)
    {
        string slot = episode.Absolute is null
            ? episode.Key.ToString()
            // Both forms, because both are what the release will be called and
            // an owner comparing the page against a site needs the one the site
            // uses.
            : $"{episode.Key} ({episode.Absolute})";

        return $"{episode.ShowTitle} {slot}";
    }

    private static string Id(TrackedEpisode episode)
    {
        return $"{episode.Key.ShowId}-{episode.Key.Season}-{episode.Key.Number}";
    }
}
