namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Whether anybody is looking at this plugin's pages, known without asking.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The flood the owner saw.</strong> A running download changes what the
/// Downloads page draws every second, every change was a push, and every push
/// makes the web app fetch the whole view again over HTTP — a page load a second
/// per open tab, and the same work pushed to nobody on a server with no tab open.
/// </para>
/// <para>
/// <strong>The hub cannot say who is watching.</strong> <c>PluginHub.Subscribe</c>
/// adds a connection to <c>plugin:{ulid}</c> and tells the plugin nothing. It does
/// not need to. A page being fetched is the proof somebody is looking, and a page
/// that is open fetches itself again on every push — so a push nothing fetches
/// after is a push nobody saw.
/// </para>
/// <para>
/// Three states, and every move between them is something that happened: a page
/// fetched, a push unanswered, a stretch with nothing to push, or the torrent
/// client doing something. Two one-shot timers measure the two stretches, and
/// neither repeats.
/// </para>
/// </remarks>
public sealed class Onlookers : IDisposable
{
    /// <summary>How long a push may go unanswered before nobody is taken to be looking.</summary>
    /// <remarks>
    /// The answer is the page fetching itself again: a push is coalesced for a
    /// second, crosses the socket, and the view is fetched over HTTP. That is a
    /// fraction of a second on the owner's network, and this is long enough for
    /// a phone on a poor connection to manage it too.
    /// </remarks>
    public static readonly TimeSpan Answer = TimeSpan.FromSeconds(15);

    /// <summary>How long with nothing to push before the looking rests.</summary>
    /// <remarks>
    /// Without it a tab closed over a list that never changes would never be
    /// found out — no change is no push, and no push is no unanswered push — and
    /// the client would be read once a second for as long as the server ran.
    /// Resting is not leaving: see <see cref="Stirred"/>.
    /// </remarks>
    public static readonly TimeSpan Idle = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time;
    private readonly Lock _lock = new();

    private Seen _seen = Seen.Away;

    /// <summary>Set by a push, taken away by the page fetching itself.</summary>
    private ITimer? _answer;

    /// <summary>Set by a page being fetched, and set again by the next one.</summary>
    private ITimer? _idle;

    private bool _disposed;

    public Onlookers(TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Somebody has started looking, or started again.</summary>
    public event Action? Arrived;

    /// <summary>Nobody is looking any more, or nothing has changed for long.</summary>
    public event Action? Left;

    private enum Seen
    {
        /// <summary>Nobody has looked, or the last push went unanswered.</summary>
        Away,

        /// <summary>A page was fetched, and every push since has been answered.</summary>
        Looking,

        /// <summary>Was looking, and nothing has changed for <see cref="Idle"/>.</summary>
        Resting,
    }

    /// <summary>Whether anybody is looking now.</summary>
    public bool Present
    {
        get
        {
            lock (_lock)
            {
                return _seen == Seen.Looking;
            }
        }
    }

    /// <summary>A page was fetched.</summary>
    /// <remarks>
    /// Every page, however it was reached — the web app answering a push, the
    /// owner navigating, a prefetch. Each one is somebody with the plugin open.
    /// </remarks>
    public void Looked()
    {
        bool arrived;

        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            arrived = _seen != Seen.Looking;
            _seen = Seen.Looking;

            _answer?.Dispose();
            _answer = null;

            _idle?.Dispose();
            _idle = _time.CreateTimer(_ => Rest(), null, Idle, Timeout.InfiniteTimeSpan);
        }

        if (arrived)
        {
            Arrived?.Invoke();
        }
    }

    /// <summary>A push went out to the pages.</summary>
    /// <remarks>
    /// Starts waiting for the answer, once. A second push before the first is
    /// answered changes nothing: either the page is there and answers both, or it
    /// is not and the first one says so.
    /// </remarks>
    public void Told()
    {
        lock (_lock)
        {
            if (_disposed || _seen != Seen.Looking || _answer is not null)
            {
                return;
            }

            _answer = _time.CreateTimer(_ => Unanswered(), null, Answer, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>The torrent client did something a page would show.</summary>
    /// <remarks>
    /// <para>
    /// Brings resting looking back, and nothing else. A page that rested may very
    /// well still be open — nothing changed, so nothing asked it anything — and a
    /// stalled torrent that starts again is exactly the one the owner was staring
    /// at. `S11-29`.
    /// </para>
    /// <para>
    /// A page that did not answer is gone, and the client moving is no reason to
    /// think it came back; only a page being fetched is. And before anybody has
    /// looked at all, a server doing its work does no page work for it.
    /// </para>
    /// </remarks>
    public void Stirred()
    {
        lock (_lock)
        {
            if (_disposed || _seen != Seen.Resting)
            {
                return;
            }

            _seen = Seen.Looking;

            _idle?.Dispose();
            _idle = _time.CreateTimer(_ => Rest(), null, Idle, Timeout.InfiniteTimeSpan);
        }

        Arrived?.Invoke();
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

            _answer?.Dispose();
            _answer = null;
            _idle?.Dispose();
            _idle = null;
        }
    }

    /// <summary>A push went unanswered, so the page it was for is not there.</summary>
    private void Unanswered()
    {
        lock (_lock)
        {
            if (_disposed || _seen != Seen.Looking)
            {
                return;
            }

            _seen = Seen.Away;

            _answer?.Dispose();
            _answer = null;
            _idle?.Dispose();
            _idle = null;
        }

        Left?.Invoke();
    }

    /// <summary>Nothing has changed for long enough that there is nothing to keep watch over.</summary>
    private void Rest()
    {
        lock (_lock)
        {
            if (_disposed || _seen != Seen.Looking)
            {
                return;
            }

            _seen = Seen.Resting;

            _answer?.Dispose();
            _answer = null;
            _idle?.Dispose();
            _idle = null;
        }

        Left?.Invoke();
    }
}
