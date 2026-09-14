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
    private readonly Func<bool> _watching;
    private readonly Action _moved;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    /// <summary>The beat, while somebody is looking, and nothing otherwise.</summary>
    /// <remarks>
    /// This was set in the constructor to go off every second for the life of
    /// the server. Gating what a beat did stopped the work and not the waking:
    /// once a second, for ever, on every server, it woke to find it had nothing
    /// to do. It exists only between <see cref="StartBeating"/> and <see cref="StopBeating"/>
    /// now, which the plugin ties to somebody having a page open.
    /// </remarks>
    private ITimer? _timer;

    private readonly Lock _lock = new();

    /// <summary>What the pages were last drawn with, or told: a reading is news only where it differs.</summary>
    private string? _last;

    /// <summary>When the look that is out began, or null while none is.</summary>
    private DateTimeOffset? _lookingSince;

    private bool _saidStuck;
    private bool _disposed;

    /// <param name="drawn">What the pages draw about the client, as one value; null when there is no client.</param>
    /// <param name="watching">
    /// Whether there is anything to look at. Asked before every reading, and
    /// the reason is that a reading is not free: it takes the client's lock and
    /// builds a string over every torrent it holds. This ran once a second from
    /// the moment the client started, on every server, whether anything was
    /// downloading or not — work for nobody, for as long as the server was up.
    /// <c>BittorrentEngine.Watching</c> answers this and had been wired to
    /// nothing since it was written.
    /// </param>
    /// <param name="moved">Told when that value changes.</param>
    /// <param name="logger">Where a client that stops answering is said.</param>
    /// <param name="time">The clock, and what wakes the look.</param>
    public Heartbeat(
        Func<string?> drawn,
        Func<bool> watching,
        Action moved,
        ILogger logger,
        TimeProvider? time = null)
    {
        _drawn = drawn;
        _watching = watching;
        _moved = moved;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>What a page was just drawn with, so a beat is compared with that.</summary>
    /// <remarks>
    /// <para>
    /// A page that has just been fetched shows what it shows, and pushing that
    /// to it again is a whole fetch of the view for nothing. Only a reading that
    /// differs from what it was drawn with is news.
    /// </para>
    /// <para>
    /// <strong>This replaced "the first beat after a start pushes nothing",
    /// which was wrong.</strong> A rate is measured between two readings, and
    /// with nobody looking the client can go unread for hours, so the page
    /// fetched after that draws the average over those hours. The first beat
    /// measures the real rate a second later; taken as a baseline it matched the
    /// beats after it, nothing was pushed, and a stalled torrent went on showing
    /// a speed it had not had for an hour. Compared with what the page drew, that
    /// first beat differs and the page is put right.
    /// </para>
    /// </remarks>
    public void Shown(string? drawn)
    {
        if (drawn is null)
        {
            return;
        }

        lock (_lock)
        {
            _last = drawn;
        }
    }

    /// <summary>Starts beating, if it is not already.</summary>
    /// <remarks>
    /// A page has been opened. The first beat is one interval away rather than
    /// now: the page that was just fetched already shows what is true, and a
    /// reading taken this instant would say the same thing.
    /// </remarks>
    public void StartBeating()
    {
        lock (_lock)
        {
            if (_disposed || _timer is not null)
            {
                return;
            }


            _timer = _time.CreateTimer(_ => Tick(), null, Every, Every);
        }
    }

    /// <summary>Stops beating. Nothing is read and nothing is pushed until it starts again.</summary>
    public void StopBeating()
    {
        ITimer? stopping;

        lock (_lock)
        {
            stopping = _timer;
            _timer = null;
        }

        stopping?.Dispose();
    }

    public void Dispose()
    {
        ITimer? stopping;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            stopping = _timer;
            _timer = null;
        }

        stopping?.Dispose();
    }

    private void Tick()
    {
        DateTimeOffset now = _time.GetUtcNow();

        // Nothing held is nothing to read. Asked before the lock and before the
        // reading, because the reading is the cost: a client with no torrents
        // still has its lock taken and a string built every second otherwise.
        //
        // Watching, never moving. A stalled download is the one being stared
        // at, and its peers, seeds and chokes are what say what is happening to
        // it while no byte does - S11-29.
        if (!Watching())
        {
            return;
        }

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

    /// <summary>Whether there is anything to look at, never letting a fault stop the beat.</summary>
    /// <remarks>
    /// A client that cannot say is looked at rather than skipped: being wrong
    /// here costs one reading, and being wrong the other way stops the pages
    /// moving with no line anywhere to say why.
    /// </remarks>
    private bool Watching()
    {
        try
        {
            return _watching();
        }
        catch (Exception wrong)
        {
            _logger.LogWarning(wrong, "The torrent client could not say whether it is holding anything.");

            return true;
        }
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
