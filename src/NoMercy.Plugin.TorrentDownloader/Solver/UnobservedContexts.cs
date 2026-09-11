using System.Reflection;
using System.Runtime.CompilerServices;
using PuppeteerSharp;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// Reads the task PuppeteerSharp fails every time a page throws its execution
/// context away, so it is never reported as a task nobody observed.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Why this reaches inside PuppeteerSharp.</strong> Every navigation
/// clears a frame's context in <c>IsolatedWorld.ClearContext</c>: it fails the
/// pending <c>_contextResolveTaskWrapper</c> with "Execution Context was
/// destroyed" and replaces it on the next line. When nothing was waiting on it
/// — a Cloudflare challenge navigates twice before anything asks — the failed
/// task is dropped, the finalizer reports it, and the media server writes an
/// <c>UnobservedTaskException</c> into its log. Eight of them in one warm-up,
/// measured on the owner's server on 11 September 2026; still in
/// PuppeteerSharp's current source. The owner chose this way of silencing them.
/// </para>
/// <para>
/// A continuation that reads the exception is attached to that task as soon as
/// it exists: when a frame is first seen, and again on <c>ContextCleared</c>,
/// which PuppeteerSharp raises right after it has made the next one. Nothing is
/// changed about how PuppeteerSharp behaves; the failure is only read.
/// </para>
/// <para>
/// Pinned to PuppeteerSharp 25.6.0. If a later version renames any of it this
/// does nothing at all rather than fail, and
/// <c>PuppeteerSharpInternalsTests</c> goes red, so an update cannot bring the
/// lines back without anyone knowing.
/// </para>
/// </remarks>
public static class UnobservedContexts
{
    private const BindingFlags Inside = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly PropertyInfo? MainWorld = typeof(Frame).GetProperty("MainWorld", Inside);

    private static readonly PropertyInfo? PuppeteerWorld = typeof(Frame).GetProperty("PuppeteerWorld", Inside);

    private static readonly FieldInfo? Pending = MainWorld?.PropertyType.GetField("_contextResolveTaskWrapper", Inside);

    private static readonly EventInfo? Cleared = MainWorld?.PropertyType.GetEvent("ContextCleared", Inside);

    private static readonly PropertyInfo? FramesOfPage =
        typeof(IPage).Assembly.GetType("PuppeteerSharp.Cdp.CdpPage")?.GetProperty("FrameManager", Inside);

    private static readonly FieldInfo? ContextsOfFrames =
        FramesOfPage?.PropertyType.GetField("_contextIdToContext", Inside);

    private static readonly PropertyInfo? WorldOfContext =
        typeof(PuppeteerSharp.ExecutionContext).GetProperty("World", Inside);

    /// <summary>Whether everything this reaches for is where it was in 25.6.0.</summary>
    public static bool Reachable =>
        MainWorld is not null
        && PuppeteerWorld is not null
        && Pending is not null
        && Cleared?.GetAddMethod(nonPublic: true) is not null
        && FramesOfPage is not null
        && ContextsOfFrames is not null
        && WorldOfContext is not null;

    /// <summary>The worlds already watched, so none is watched twice.</summary>
    /// <remarks>Weak, so a closed page's worlds are not kept alive by being remembered.</remarks>
    private static readonly ConditionalWeakTable<object, object> Watched = new();

    private static readonly object Seen = new();

    /// <summary>Watches every frame the page has and every frame it gets later.</summary>
    /// <remarks>
    /// <para>
    /// Looked over again on everything the page does, not only when a frame is
    /// attached. A challenge's frame is served from another origin, so Chrome
    /// moves it to a process of its own, and PuppeteerSharp answers by giving
    /// that frame brand-new worlds (<c>CdpFrame.UpdateClient</c>). Watching only
    /// the worlds a frame had when it was attached halved the lines in the
    /// owner's log and no more: eight became four, measured 11 September 2026.
    /// </para>
    /// <para>
    /// Every request and response is often enough to see a new world before it
    /// is cleared, and cheap: a world already watched is found in
    /// <see cref="Watched"/> and left alone.
    /// </para>
    /// </remarks>
    public static void Watch(IPage page)
    {
        if (!Reachable)
        {
            return;
        }

        Scan(page);

        page.FrameAttached += (_, _) => Scan(page);
        page.FrameNavigated += (_, _) => Scan(page);
        page.Request += (_, _) => Scan(page);
        page.Response += (_, _) => Scan(page);

        // And on a short clock while the page is open. A frame Chrome has moved
        // to a process of its own reports its events to its own session, not to
        // the page, so none of the four above fires when PuppeteerSharp gives it
        // new worlds — measured: with site isolation switched off, which keeps
        // every frame in one process, not one line reached the log, and with it
        // on, four to six did. Site isolation stays on: without it TorrentBay's
        // challenge cleared without a cookie, twice. A tab lives for seconds, so
        // looking every few milliseconds costs nothing worth counting.
        int scanning = 0;

        Timer clock = new(
            _ =>
            {
                if (page.IsClosed || Interlocked.Exchange(ref scanning, 1) == 1)
                {
                    return;
                }

                try
                {
                    Scan(page);
                }
                finally
                {
                    Volatile.Write(ref scanning, 0);
                }
            },
            null,
            TimeSpan.Zero,
            Every);

        page.Close += (_, _) => clock.Dispose();
    }

    /// <summary>How often an open page is looked over for worlds nothing watches yet.</summary>
    private static readonly TimeSpan Every = TimeSpan.FromMilliseconds(10);

    private static void Scan(IPage page)
    {
        IFrame[] frames;

        try
        {
            frames = page.Frames;
        }
        catch (Exception closing) when (closing is PuppeteerException or ObjectDisposedException)
        {
            // The page is on its way out, and so are its worlds.
            return;
        }

        foreach (IFrame frame in frames)
        {
            WatchFrame(frame);
        }

        // And every world PuppeteerSharp still holds a context for. When a frame
        // moves to a process of its own its old worlds are replaced, but they
        // stay in this list until the old process says its contexts are gone —
        // and that clears them a second time, which is the call that fails a
        // task nothing is watching. Watching the frames alone left four of the
        // eight lines standing.
        if (FramesOfPage!.DeclaringType?.IsInstanceOfType(page) == true
            && FramesOfPage.GetValue(page) is object manager
            && ContextsOfFrames!.GetValue(manager) is System.Collections.IDictionary contexts)
        {
            foreach (object? context in contexts.Values)
            {
                if (context is not null && WorldOfContext!.GetValue(context) is object world)
                {
                    WatchWorld(world);
                }
            }
        }
    }

    private static void WatchFrame(IFrame? frame)
    {
        if (frame is not Frame known)
        {
            return;
        }

        foreach (PropertyInfo world in (PropertyInfo[])[MainWorld!, PuppeteerWorld!])
        {
            if (world.GetValue(known) is object isolated)
            {
                WatchWorld(isolated);
            }
        }
    }

    private static void WatchWorld(object world)
    {
        lock (Seen)
        {
            if (Watched.TryGetValue(world, out _))
            {
                return;
            }

            Watched.Add(world, Seen);
        }

        Observe(world);

        // Raised after the next task has been made, so that one is watched
        // before anything can fail it.
        EventHandler cleared = (sender, _) =>
        {
            if (sender is not null)
            {
                Observe(sender);
            }
        };

        Cleared!.GetAddMethod(nonPublic: true)!.Invoke(world, [cleared]);
    }

    private static void Observe(object world)
    {
        object? source = Pending!.GetValue(world);

        if (source?.GetType().GetProperty("Task")?.GetValue(source) is Task task)
        {
            _ = task.ContinueWith(
                static failed => _ = failed.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }
}
