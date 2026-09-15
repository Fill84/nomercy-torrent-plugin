namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// Where every stage says what it is doing.
/// </summary>
/// <remarks>
/// Called from whatever thread a stage happens to be on — the feeds are read all
/// at once, and so are the indexers after the first-choice ones — so every
/// implementation has to be safe under all of them at once.
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

    /// <summary>
    /// A search run has begun: its counters start from nought and nothing is
    /// noted about any episode yet.
    /// </summary>
    void RunStarted();

    /// <summary>
    /// The run is over, however it ended. Nothing of it stays in flight, its
    /// notes go and its counters with them.
    /// </summary>
    void RunEnded();

    /// <summary>One more of <paramref name="counter"/> in this run, or <paramref name="by"/> more.</summary>
    void Counted(RunCounter counter, int by = 1);

    /// <summary>
    /// A line about what the run did for <paramref name="episode"/>, shown until
    /// that episode is decided.
    /// </summary>
    void Noted(ActivityStage stage, string episode, string line);

    /// <summary>Everything happening now and lately, frozen.</summary>
    ActivitySnapshot Snapshot();
}
