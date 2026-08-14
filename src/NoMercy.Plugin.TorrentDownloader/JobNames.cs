namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// The four cadences, by name, and the cron each one runs on by default.
/// </summary>
/// <remarks>
/// A job's name reaches the server as <c>plugin:{id}:{name}</c> and comes back
/// on every tick, so these strings are part of what the owner sees in the job
/// list. They are constants because the name in <see cref="TorrentDownloaderPlugin.Jobs"/>
/// and the name matched on a tick have to be the same string, and a typo in a
/// literal would look like a job that simply never ran.
/// </remarks>
public static class JobNames
{
    /// <summary>Watch what is downloading; stage and dispatch what finished.</summary>
    public const string Transfers = "transfers";

    /// <summary>Read every feed into the name pool.</summary>
    public const string Feed = "feed";

    /// <summary>Resolve names for missing episodes, find copies, grab.</summary>
    public const string Search = "search";

    /// <summary>Re-derive the missing list from the library, prune, re-verify.</summary>
    public const string Maintenance = "maintenance";

    public const string TransfersCron = "* * * * *";
    public const string FeedCron = "*/15 * * * *";
    public const string SearchCron = "0 */6 * * *";
    public const string MaintenanceCron = "0 4 * * *";

    public static IReadOnlySet<string> All { get; } =
        new HashSet<string>(StringComparer.Ordinal) { Transfers, Feed, Search, Maintenance };
}
