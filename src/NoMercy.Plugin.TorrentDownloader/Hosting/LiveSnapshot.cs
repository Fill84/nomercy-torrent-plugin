using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Pushes the snapshot to every open page, at most once every 250 ms.
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
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromMilliseconds(250);

    private readonly IPluginHubContext _hub;
    private readonly IActivityJournal _journal;
    private readonly ILogger _logger;
    private readonly Func<CycleStatus> _cycle;
    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private ITimer? _due;
    private bool _disposed;

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

    /// <summary>What every page renders from.</summary>
    public sealed record Payload(ActivitySnapshot Activity, CycleStatus Cycle);

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
        Payload payload = new(_journal.Snapshot(), _cycle());

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
