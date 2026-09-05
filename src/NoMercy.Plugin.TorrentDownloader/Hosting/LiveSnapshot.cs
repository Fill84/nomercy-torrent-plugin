using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Pushes the snapshot to every open page, at most once a second.
/// </summary>
/// <remarks>
/// No page polls. A cycle publishes constantly — every source answering, every
/// episode moving a stage — so a push per change would have the pages
/// re-rendering faster than a person can read and the server carrying all of
/// it. Coalescing means a burst costs one message, and the message carries the
/// state after the last change in it rather than the first.
/// </remarks>
public sealed class LiveSnapshot : IDisposable
{
    /// <summary>The channel every page subscribes to.</summary>
    public const string Channel = "torrent-downloader:changed";

    /// <summary>The floor between two pushes.</summary>
    /// <remarks>
    /// A second, because a push costs the client a whole page. The message
    /// carries the snapshot, but the host that draws a plugin does not read it:
    /// any message means "something moved", and it answers by re-reading the
    /// entire view over HTTP, translations and all. It has no other option — a
    /// payload is this plugin's own shape and that host draws every plugin.
    ///
    /// A download in flight moves its byte count on every tick, so the changes
    /// never stop, and at a quarter of a second that was four complete page
    /// reads a second: the pages flickered for as long as anything was
    /// downloading.
    /// </remarks>
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(1);

    private readonly IPluginHubContext _hub;
    private readonly IActivityJournal _journal;
    private readonly ILogger _logger;
    private readonly Func<CycleStatus> _cycle;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private ITimer? _due;
    private bool _disposed;

    /// <summary>What the pages were last told, so only changes are sent.</summary>
    private IReadOnlyList<ActivityEvent> _flight = [];

    private CycleStatus? _standing;

    /// <summary>Whether two lists of events say the same thing.</summary>
    /// <remarks>
    /// By value, because a snapshot is a fresh list every time and comparing
    /// the references would call every push a change — which is the whole of
    /// what this is here to avoid.
    /// </remarks>
    private static bool Same(IReadOnlyList<ActivityEvent> now, IReadOnlyList<ActivityEvent> before)
    {
        if (now.Count != before.Count)
        {
            return false;
        }

        for (int at = 0; at < now.Count; at++)
        {
            if (now[at] != before[at])
            {
                return false;
            }
        }

        return true;
    }

    public LiveSnapshot(
        IPluginHubContext hub,
        IActivityJournal journal,
        ILogger logger,
        Func<CycleStatus> cycle,
        TimeProvider? time = null)
    {
        _hub = hub;
        _journal = journal;
        _logger = logger;
        _cycle = cycle;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What changed since the last push, and nothing else.</summary>
    /// <param name="InFlight">
    /// The work that started and has not finished, or <c>null</c> when it is
    /// what it was last time. This used to be sent on every push whether it had
    /// moved or not, so a torrent ticking its byte count re-sent a list of jobs
    /// that had not changed since the page was opened.
    /// </param>
    /// <param name="Cycle">
    /// Where the search cycle stands, or <c>null</c> when it has not moved.
    /// </param>
    /// <param name="At">
    /// When this push was made, and the reason it is here: a download moves its
    /// byte count without touching the journal or the cycle, so a payload of
    /// only-what-changed would otherwise be empty and identical to the last
    /// one. A receiver with any reason to skip a message it has already seen
    /// would draw the figures of the moment the page was opened and never move
    /// again — which is exactly what the owner saw. This differs on every push,
    /// so no two are the same message.
    /// </param>
    public sealed record Payload(
        IReadOnlyList<ActivityEvent>? InFlight,
        CycleStatus? Cycle,
        DateTimeOffset At);

    /// <summary>
    /// Something moved. The push follows within <see cref="MinimumInterval"/>;
    /// every other change until then rides along on it.
    /// </summary>
    public void Changed()
    {
        lock (_lock)
        {
            // Nothing is scheduled while nothing has changed, so a quiet plugin
            // sends nothing at all — a ticker pushing an unchanged snapshot
            // four times a second would be the poll this design exists to
            // avoid, just written at the other end.
            if (_disposed || _due is not null)
            {
                return;
            }

            _due = _time.CreateTimer(_ => Push(), null, MinimumInterval, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _due?.Dispose();
            _due = null;
        }
    }

    private void Push()
    {
        lock (_lock)
        {
            _due?.Dispose();
            _due = null;

            if (_disposed)
            {
                return;
            }
        }

        // Read outside the lock: the journal takes its own, and holding two in
        // one order here and the other order there is how a deadlock is built.
        ActivitySnapshot taken = _journal.Snapshot();
        CycleStatus cycle = _cycle();

        // Only what moved. The history is never sent at all — five hundred
        // events, about a hundred kilobytes, on every push, read by nobody —
        // and the work in flight and the cycle are sent only where they differ
        // from what the pages were last told.
        IReadOnlyList<ActivityEvent>? flight = Same(taken.InFlight, _flight) ? null : taken.InFlight;
        CycleStatus? moved = cycle == _standing ? null : cycle;

        _flight = taken.InFlight;
        _standing = cycle;

        Payload payload = new(flight, moved, _time.GetUtcNow());

        _ = Send(payload);
    }

    private async Task Send(Payload payload)
    {
        try
        {
            await _hub.PushAsync(Channel, payload);
        }
        catch (Exception exception)
        {
            // This runs on a timer, so there is no caller to catch it and an
            // escaping exception takes the media server down rather than the
            // plugin. A client that closed its tab mid-push is ordinary, and a
            // page that missed one push is repaired by the next.
            _logger.LogWarning(exception, "Could not push the snapshot to the open pages.");
        }
    }
}
