using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// What is being looked for, what has been given up on for now, and what has
/// not aired yet — three lists, never one.
/// </summary>
/// <remarks>
/// Separated because they answer different questions and mixing them makes both
/// answers wrong: an unaired episode counted among the missing is work the
/// plugin is not doing, and one left out of every list altogether is an episode
/// nobody can see has stopped moving.
/// </remarks>
public static class QueueView
{
    public const string LookingTableId = "looking";
    public const string GivenUpTableId = "givenup";
    public const string WaitingTableId = "waiting";

    public static PluginView Render(IReadOnlyList<TrackedEpisode> tracked)
    {
        return new()
        {
            Layout = PluginLayout.ListDetail,
            Components =
            [
                PluginViews.Text("looking-heading", "Looking", "title"),
                Looking(QueueOrder.Order(tracked)),

                PluginViews.Text("givenup-heading", "Given up for now", "title"),
                GivenUp(tracked.Where(episode => episode.State == EpisodeState.Unavailable)),

                PluginViews.Text("waiting-heading", "Waiting to air", "title"),
                Waiting(tracked.Where(episode => episode.State == EpisodeState.NotAired)),
            ],
        };
    }

    /// <remarks>
    /// In the order the search cadence will actually ask, from the same rule it
    /// uses — a page that showed a different order would be a guess about what
    /// the plugin is about to do.
    /// </remarks>
    private static PluginComponent Looking(IReadOnlyList<TrackedEpisode> ordered)
    {
        return PluginViews.Table(
            LookingTableId,
            [
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "attempts", Label = "Attempts" },
                new() { Key = "last", Label = "Last tried" },
            ],
            [
                .. ordered.Select(episode => PluginViews.Row(
                    $"{LookingTableId}-{Id(episode)}",
                    new Dictionary<string, object?>
                    {
                        ["episode"] = Name(episode),
                        ["attempts"] = episode.Attempts,
                        // Never searched is not the same as searched long ago,
                        // and nought would be a date.
                        ["last"] = episode.LastSearchAt?.ToString("u") ?? "never",
                    })),
            ],
            "Nothing is being looked for.");
    }

    private static PluginComponent GivenUp(IEnumerable<TrackedEpisode> episodes)
    {
        return PluginViews.Table(
            GivenUpTableId,
            [
                new() { Key = "episode", Label = "Episode" },
                new() { Key = "attempts", Label = "Attempts" },
            ],
            [
                .. episodes.Select(episode => PluginViews.Row(
                    $"{GivenUpTableId}-{Id(episode)}",
                    new Dictionary<string, object?>
                    {
                        ["episode"] = Name(episode),
                        ["attempts"] = episode.Attempts,
                    })),
            ],
            // It says "for now" because it is: the next maintenance pass
            // re-derives from the library and puts these back to missing.
            "Nothing has been given up on.");
    }

    private static PluginComponent Waiting(IEnumerable<TrackedEpisode> episodes)
    {
        return PluginViews.Table(
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
                    .Select(episode => PluginViews.Row(
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
