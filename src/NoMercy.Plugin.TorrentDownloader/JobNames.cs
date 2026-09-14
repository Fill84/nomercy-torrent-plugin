using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// What the plugin's own clock keeps, and what the host is told to tick.
/// </summary>
/// <remarks>
/// <para>
/// <strong>There were four of these and now there is one.</strong> Transfers,
/// feed, search and maintenance each had a name and a cron of its own, and none
/// of them was a schedule: they are the steps of one cycle, started by each
/// other. Transfers in particular was <c>* * * * *</c> — and not because
/// anything about transfers wanted a minute, but because the torrent client did
/// all its housekeeping inside the method the pages call to draw a table, so
/// without a tick a minute it stopped expiring magnets, noticing stalls,
/// noticing completions and writing resume files. `S12-13` moved those to their
/// own moments, which is what let this go.
/// </para>
/// <para>
/// The host is still told to tick this plugin, because the contract has no way
/// to decline: a plugin declaring no jobs is registered under its single
/// <c>CronExpression</c> instead. That tick does one thing — it makes sure the
/// plugin's own clock is armed — and starts nothing. The clock is what starts a
/// cycle, and it is set to the moment the next one is due rather than woken to
/// ask whether one is.
/// </para>
/// </remarks>
public static class JobNames
{
    /// <summary>The one job, and the one cadence the owner sets.</summary>
    public const string Cycle = Cadences.Name;

    /// <summary>
    /// What the host is told, and it is not the owner's cadence.
    /// </summary>
    /// <remarks>
    /// Hourly, and it starts nothing. The owner's own cadence may be every ten
    /// minutes or once a day and it is kept by this plugin's clock, which the
    /// host cannot be told about: a schedule is read from a plugin when the
    /// plugin loads and never asked for again.
    /// </remarks>
    public const string HostCron = Cadences.Hourly;

    /// <summary>The four this plugin used to declare, and no longer does.</summary>
    /// <remarks>
    /// Accepted rather than thrown at. The host registers a plugin's jobs when
    /// the plugin loads and removes them by the names the loaded instance
    /// declares, so an upgrade can leave the previous four registrations in the
    /// queue with nothing left to take them out. A tick under one of those is
    /// an upgrade window, not a fault, and a plugin that threw would write four
    /// stack traces an hour into the owner's log for as long as it lasted.
    /// </remarks>
    public static IReadOnlySet<string> Retired { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "transfers", "feed", "search", "maintenance" };

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Cycle };

    /// <summary>Whether this plugin will answer to a name at all.</summary>
    public static bool Answers(string name) => All.Contains(name) || Retired.Contains(name);
}
