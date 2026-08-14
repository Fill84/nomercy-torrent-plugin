namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// Where the search cycle stands: the part of the status bar the journal cannot
/// answer, because a cadence's timing belongs to the host that registered it.
/// </summary>
/// <param name="Running">Whether a cycle is running now.</param>
/// <param name="LastRanAt">
/// When the last one finished, or null when none ever has. Null is not zero:
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
}
