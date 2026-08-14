namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// Where every stage says what it is doing.
/// </summary>
/// <remarks>
/// Called from whatever thread a stage happens to be on — harvest fans out over
/// every feed, find over every indexer, and stages two to six run per episode
/// at once — so every implementation has to be safe under all of them at once.
/// </remarks>
public interface IActivityJournal
{
    /// <summary>
    /// Work has begun on <paramref name="subject"/>. It stays in flight until
    /// <see cref="Finished"/> or <see cref="Failed"/> names the same stage and
    /// the same subject.
    /// </summary>
    void Started(ActivityStage stage, string subject, string? detail = null);

    void Finished(ActivityStage stage, string subject, string? detail = null);

    /// <summary>
    /// Work stopped. The detail is not optional here: a failure with no reason
    /// is the one thing the owner opened the page to find out.
    /// </summary>
    void Failed(ActivityStage stage, string subject, string detail);

    /// <summary>Everything happening now and lately, frozen.</summary>
    ActivitySnapshot Snapshot();
}
