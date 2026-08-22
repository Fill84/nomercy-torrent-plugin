namespace NoMercy.Plugin.TorrentDownloader.Core.Activity;

/// <summary>
/// The journal every stage reports to, and every page renders from.
/// </summary>
/// <remarks>
/// One lock over both collections rather than two concurrent ones. A snapshot
/// has to show an in-flight list and a history that agree with each other: with
/// a lock per collection, a snapshot taken while a subject was finishing could
/// show it both still running and already done, and that page would be read as
/// a stuck episode.
/// </remarks>
public sealed class ActivityJournal : IActivityJournal
{
    /// <summary>
    /// How many past events are kept.
    /// </summary>
    /// <remarks>
    /// The journal lives as long as the server does, so an unbounded history is
    /// a leak that only shows after a week of running — which is exactly when
    /// nobody is watching it. Five hundred is several cycles' worth, which is
    /// as far back as "what happened to this episode" is ever asked.
    /// </remarks>
    public const int HistoryLimit = 500;

    private readonly Lock _lock = new();
    private readonly Dictionary<(ActivityStage Stage, string Subject), ActivityEvent> _inFlight = [];
    private readonly Queue<ActivityEvent> _history = new(HistoryLimit);
    private readonly TimeProvider _time;

    public ActivityJournal(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>
    /// Raised after anything is recorded, so an open page can be sent the new
    /// state.
    /// </summary>
    /// <remarks>
    /// Nothing told the pages. <c>LiveSnapshot</c> was written, tested and
    /// wired to a hub, and the one method that starts a push was called by no
    /// code at all — so a dashboard opened during a cycle showed the stage the
    /// plugin was on when the page loaded and never moved again. Its own tests
    /// called that method by hand, which is why they passed throughout.
    /// </remarks>
    public event Action? Recorded;

    public void Started(ActivityStage stage, string subject, string? detail = null)
    {
        Record(new(stage, ActivityOutcome.Started, subject, _time.GetUtcNow(), detail));
    }

    public void Finished(ActivityStage stage, string subject, string? detail = null)
    {
        Record(new(stage, ActivityOutcome.Finished, subject, _time.GetUtcNow(), detail));
    }

    public void Failed(ActivityStage stage, string subject, string detail)
    {
        Record(new(stage, ActivityOutcome.Failed, subject, _time.GetUtcNow(), detail));
    }

    public ActivitySnapshot Snapshot()
    {
        lock (_lock)
        {
            // Copied, not wrapped. A read-only view over the live collections
            // would change under whoever was handed it, and the page reading it
            // would see a list mutate mid-render.
            return new(
                [.. _inFlight.Values.OrderBy(activity => activity.At)],
                [.. _history],
                _time.GetUtcNow());
        }
    }

    private void Record(ActivityEvent activity)
    {
        Store(activity);

        // Outside the lock. A listener that pushes takes its own, and holding
        // two in one order here and the other order there is how a deadlock is
        // built — which this plugin has already shipped once.
        Recorded?.Invoke();
    }

    private void Store(ActivityEvent activity)
    {
        lock (_lock)
        {
            (ActivityStage Stage, string Subject) key = (activity.Stage, activity.Subject);

            if (activity.Outcome == ActivityOutcome.Started)
            {
                _inFlight[key] = activity;
            }
            else
            {
                // Both endings clear it: a stage that failed is not still
                // running, and leaving it in flight would show as stuck for as
                // long as the server stayed up.
                _inFlight.Remove(key);
            }

            _history.Enqueue(activity);

            while (_history.Count > HistoryLimit)
            {
                _history.Dequeue();
            }
        }
    }
}
