// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Turning what the store keeps into what a reader wants.
///
/// <para>
/// All of this was private to the downloads page while the downloads page was the only page.
/// Six pages now show the same episode slot, the same "3 hours ago" and the same rate, and
/// six copies of "how do we spell a season and episode" is how one page ends up reading
/// <c>S1E7</c> while its neighbour reads <c>S01E07</c>.
/// </para>
/// </summary>
internal static class Format
{
    /// <summary>
    /// Which episode, for a reader.
    ///
    /// <para>
    /// Not <see cref="EpisodeKey.ToString"/>: that leads with the show id so a log line is
    /// unambiguous on its own. On a page the show's name is the text beside this, so the id
    /// is a number the reader has no use for - and it was on screen as "456 S00E01" until
    /// somebody looked.
    /// </para>
    /// </summary>
    public static string Slot(EpisodeKey key) => $"S{key.Season:D2}E{key.Episode:D2}";

    /// <summary>A heading that carries its own count, so an empty section reads as empty rather than as unloaded.</summary>
    public static string Count(string heading, int count) => count == 0 ? heading : $"{heading} ({count})";

    public static string Peers(int peers) => peers == 1 ? "1 peer" : $"{peers} peers";

    public static string Percentage(Transfer transfer) =>
        transfer.BytesTotal > 0 ? $"{transfer.Progress * 100:0}%" : "starting";

    /// <summary>
    /// What this download is bringing.
    ///
    /// <para>
    /// A season pack labelled with the one episode that triggered it reads as a single
    /// episode arriving, which misleads about both the size of the download and how much of
    /// the queue it is about to clear.
    /// </para>
    /// </summary>
    public static string Covers(Grab grab) =>
        grab.Covered.Count > 1
            ? $"{grab.Covered.Count} episodes"
            : Slot(grab.Key);

    /// <summary>
    /// Whether this one has not aired. It stays on the queue either way - what is coming is
    /// exactly what its owner wants to see - but it says so rather than sitting there looking
    /// like something the plugin is failing to find.
    /// </summary>
    public static bool NotOutYet(WantedEpisode episode) =>
        episode.AirDate is DateOnly airs && airs > DateOnly.FromDateTime(DateTime.UtcNow);

    /// <summary>
    /// The rate and what is left of the wait, or a plain word when there is no honest number
    /// to give. "Stalled" is more use than "0.0 MB/s", and an estimate off a rate of nothing
    /// is not an estimate.
    /// </summary>
    public static string Rate(Transfer transfer)
    {
        if (transfer.Paused)
            return "Paused";

        if (transfer.BytesPerSecond <= 0)
            return "Stalled";

        string rate = $"{transfer.BytesPerSecond / (1024d * 1024d):0.0} MB/s";

        return transfer.Remaining is { } left ? $"{rate}, {Left(left)} left" : rate;
    }

    /// <summary>Everything moving at once, which is the number an owner actually watches.</summary>
    public static string? TotalRate(IReadOnlyList<Transfer> transfers)
    {
        long total = transfers.Where(transfer => !transfer.Paused).Sum(transfer => transfer.BytesPerSecond);

        return total > 0 ? $"{total / (1024d * 1024d):0.0} MB/s in total" : null;
    }

    /// <summary>
    /// How long ago, rather than a timestamp. "3 hours ago" is read at a glance; a date and
    /// time has to be subtracted from now before it means anything.
    /// </summary>
    public static string Ago(DateTimeOffset when)
    {
        TimeSpan since = DateTimeOffset.UtcNow - when;

        return since switch
        {
            { TotalDays: >= 2 } => $"{since.TotalDays:0} days ago",
            { TotalDays: >= 1 } => "yesterday",
            { TotalHours: >= 1 } => $"{since.TotalHours:0} h ago",
            { TotalMinutes: >= 1 } => $"{since.TotalMinutes:0} min ago",
            _ => "just now",
        };
    }

    public static string Until(DateTimeOffset? expires)
    {
        if (expires is null)
            return "skipped for good";

        TimeSpan left = expires.Value - DateTimeOffset.UtcNow;

        return left switch
        {
            { TotalDays: >= 2 } => $"{left.TotalDays:0} days left",
            { TotalDays: >= 1 } => "1 day left",
            { TotalHours: >= 1 } => $"{left.TotalHours:0} h left",
            _ => "nearly up",
        };
    }

    public static string Outcome(HistoryEvent outcome) => outcome switch
    {
        HistoryEvent.Imported => "Imported",
        HistoryEvent.Skipped => "Skipped",
        HistoryEvent.Failed => "Failed",
        HistoryEvent.Cancelled => "Cancelled",
        _ => "Grabbed",
    };

    /// <summary>
    /// Semantic, never a colour. What "failed" should look like is the theme's business and
    /// changes between light, dark and a television.
    /// </summary>
    public static string OutcomeVariant(HistoryEvent outcome) => outcome switch
    {
        HistoryEvent.Imported => PluginBadgeVariant.Success,
        HistoryEvent.Failed => PluginBadgeVariant.Warning,
        HistoryEvent.Skipped => PluginBadgeVariant.Warning,
        _ => PluginBadgeVariant.Neutral,
    };

    private static string Left(TimeSpan remaining) => remaining switch
    {
        { TotalDays: >= 1 } => $"{remaining.TotalDays:0} d",
        { TotalHours: >= 1 } => $"{remaining.TotalHours:0} h",
        { TotalMinutes: >= 1 } => $"{remaining.TotalMinutes:0} min",
        _ => "under a minute",
    };
}
