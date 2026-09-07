using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The once-a-second look at the client that tells the pages when to move.
/// </summary>
/// <remarks>
/// <para>
/// A look, not a push: it asks the client what the pages would draw and says
/// "something moved" only where that differs from last time. A byte count
/// ticking, a peer arriving, a torrent stalling all change it; a torrent
/// sitting still with the same peers does not.
/// </para>
/// <para>
/// <strong>One look at a time, and the timer never waits on it.</strong> The
/// look takes the client's lock, and a client that is busy on its disk holds
/// that lock for as long as the disk takes. This used to be a timer callback
/// that looked in place: every second another callback arrived, sat down
/// behind the lock on a thread of its own, and stayed. On 7 September 2026 the
/// client stopped answering at 00:03 UTC and the server made a new thread
/// every second for ninety minutes — the log shows the thread number climbing
/// from two hundred to five thousand and not one line from a thread it already
/// had — until the pool had nothing left to answer a page with and the owner
/// restarted it. A tick that finds the last look still out is dropped; the
/// look runs on a thread of its own; and a client that has not answered for
/// <see cref="Patience"/> is said so once, in the log, with its return said
/// once too, so the next hang has a first line and a last line instead of a
/// thread number.
/// </para>
/// </remarks>
public sealed class Heartbeat : IDisposable
{
    /// <summary>How often the client is looked at.</summary>
    /// <remarks>
    /// The floor a page can be drawn at, so this cannot say "moved" oftener
    /// than a page can be redrawn.
    /// </remarks>
    public static readonly TimeSpan Every = LiveSnapshot.MinimumInterval;

    /// <summary>How long a look may take before it is said to be stuck.</summary>
    /// <remarks>
    /// Long enough that a season pack being hashed does not trip it on every
    /// restart, short enough that the log names the minute a hang began.
    /// </remarks>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly Func<string?> _drawn;
    private readonly Action _moved;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly ITimer _timer;
    private readonly Lock _lock = new();

    /// <summary>What the pages were last told the client draws.</summary>
    private string? _last;

    /// <summary>When the look that is out began, or null while none is.</summary>
    private DateTimeOffset? _lookingSince;

    private bool _saidStuck;
    private bool _disposed;

    /// <param name="drawn">What the pages draw about the client, as one value; null when there is no client.</param>
    /// <param name="moved">Told when that value changes.</param>
    /// <param name="logger">Where a client that stops answering is said.</param>
    /// <param name="time">The clock, and what wakes the look.</param>
    public Heartbeat(Func<string?> drawn, Action moved, ILogger logger, TimeProvider? time = null)
    {
        _drawn = drawn;
        _moved = moved;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _timer = _time.CreateTimer(_ => Tick(), null, Every, Every);
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
        }

        _timer.Dispose();
    }

    private void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            if (_lookingSince is DateTimeOffset since)
            {
                // Dropped, not queued. The look that is out will say what it
                // finds; a second one behind the same lock would only be a
                // second thread waiting.
                if (!_saidStuck && now - since >= Patience)
                {
                    _saidStuck = true;

                    _logger.LogWarning(
                        "The torrent client has not answered for {Seconds:0} seconds, so the pages will not move until it does.",
                        (now - since).TotalSeconds);
                }

                return;
            }

            _lookingSince = now;
        }

        // On a thread of its own, never the timer's: a timer callback that
        // blocks is exactly the thread-a-second this exists to stop.
        _ = Task.Run(Look);
    }

    private void Look()
    {
        string? drawn = null;

        try
        {
            drawn = _drawn();
        }
        catch (Exception wrong)
        {
            // Said and survived. This runs on a pool thread with nobody
            // awaiting it, and a client that cannot describe itself this
            // second is not a reason to stop looking next second.
            _logger.LogWarning(wrong, "The torrent client could not say what the pages should draw.");
        }

        bool changed;
        bool wasStuck;
        TimeSpan took;

        lock (_lock)
        {
            took = _time.GetUtcNow() - (_lookingSince ?? _time.GetUtcNow());
            wasStuck = _saidStuck;
            _lookingSince = null;
            _saidStuck = false;

            changed = drawn is not null && !string.Equals(drawn, _last, StringComparison.Ordinal);

            if (changed)
            {
                _last = drawn;
            }
        }

        if (wasStuck)
        {
            _logger.LogInformation("The torrent client answered again after {Seconds:0} seconds.", took.TotalSeconds);
        }

        if (changed)
        {
            _moved();
        }
    }
}
