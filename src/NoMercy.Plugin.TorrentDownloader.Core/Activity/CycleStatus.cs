namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>How a search run ended.</summary>
public enum RunEnd
{
    /// <summary>It went through the whole queue.</summary>
    Finished,

    /// <summary>The owner pressed Stop, or the server went away.</summary>
    Stopped,

    /// <summary>Something went wrong that it could not carry on past.</summary>
    Failed,
}

/// <summary>
/// Where the search cycle stands: the part of the status bar the journal cannot
/// answer, because a cadence's timing belongs to the host that registered it.
/// </summary>
/// <param name="Running">Whether a cycle is running now.</param>
/// <param name="LastRanAt">
/// When the last one ended, or null when none ever has. Null is not zero:
/// a plugin installed this morning has never run, and a page saying "0 minutes
/// ago" would be stating the opposite.
/// </param>
/// <param name="NextDueAt">
/// When the next one is due, or null when nothing is scheduled — which is what
/// a server that has not yet registered the cadences looks like.
/// </param>
public sealed record CycleStatus(bool Running, DateTimeOffset? LastRanAt, DateTimeOffset? NextDueAt)
{
    /// <summary>Nothing known yet, which is the honest state before the first tick.</summary>
    public static CycleStatus Unknown { get; } = new(false, null, null);

    /// <summary>When the running search began, or null when none runs.</summary>
    public DateTimeOffset? StartedAt { get; init; }

    /// <summary>How the last run ended, or null when that is not known.</summary>
    /// <remarks>
    /// A stopped run is not a finished one. The owner, who pressed Stop, is the
    /// one who needs to see that the stop took.
    /// </remarks>
    public RunEnd? LastEnd { get; init; }
}
