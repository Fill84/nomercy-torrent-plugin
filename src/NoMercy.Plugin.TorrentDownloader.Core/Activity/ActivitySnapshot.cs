namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// Everything the plugin is doing and has recently done, frozen at one moment.
/// </summary>
/// <remarks>
/// Immutable, and detached from the journal that produced it. A snapshot is
/// handed to a page, rendered, pushed over the hub and compared against the
/// previous one; if the journal could still change it, two reads of the same
/// snapshot would disagree and the comparison that decides whether to push
/// would be comparing an object against itself.
/// </remarks>
/// <param name="InFlight">
/// Work that started and has not finished, oldest first. This is what "is
/// anything stuck?" is answered from.
/// </param>
/// <param name="History">The most recent events, oldest first, bounded.</param>
/// <param name="TakenAt">When this snapshot was taken.</param>
public sealed record ActivitySnapshot(
    IReadOnlyList<ActivityEvent> InFlight,
    IReadOnlyList<ActivityEvent> History,
    DateTimeOffset TakenAt)
{
    /// <summary>A snapshot of a journal that has seen nothing.</summary>
    public static ActivitySnapshot Empty { get; } = new([], [], DateTimeOffset.UnixEpoch);
}
