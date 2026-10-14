using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// The one browser this process has: installed once, hidden before it starts,
/// and kept for the life of the server.
/// </summary>
/// <remarks>
/// One for the process, not one per source. Clearance is issued per host and
/// kept in a tab per host; a second browser would solve every gate a second
/// time and put a second Chrome's worth of memory beside the media server for
/// the privilege.
/// </remarks>
public sealed class Browser(
    BrowserInstall install,
    IHiddenStageFactory stages,
    ILogger logger,
    int port = Browser.DefaultPort,
    TimeSpan? listeningWithin = null) : IDisposable
{
    /// <summary>Where its remote-debugging endpoint listens.</summary>
    public const int DefaultPort = 9222;

    /// <summary>How long the browser gets to open its debugging port.</summary>
    /// <remarks>
    /// Generous: a cold Chrome on a busy server is seconds, and the cost of
    /// waiting too little is a solve that fails for no reason anybody can see.
    /// A test that stands up no real browser passes nothing at all here.
    /// </remarks>
    private readonly TimeSpan _listeningWithin = listeningWithin ?? TimeSpan.FromSeconds(20);

    private readonly SemaphoreSlim _starting = new(1, 1);
    private IHiddenStage? _stage;
    private IBrowserProcess? _process;
    private bool _disposed;

    /// <summary>1 once the warning that no browser can be started has been logged. An int for <see cref="Interlocked"/>: searches arrive on many threads.</summary>
    private int _saidNoBrowser;

    /// <summary>
    /// Starts the browser, or answers the one already running.
    /// </summary>
    /// <returns>
    /// Null when this platform cannot hide a window. Not an exception: a plugin
    /// that cannot read gated sources still reads the other twelve, and the
    /// caller's job is to skip those sources and say so.
    /// </returns>
    public async Task<IBrowserProcess?> StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (!stages.CanHideABrowser)
        {
            // Loudly once, quietly after. Every search that meets a challenge asks for the browser, and a
            // warning per ask filled the owner's log with the same sentence a hundred times a run (GitHub
            // issue 6). The reason also stands on the Sources page, so nothing is lost by saying it once.
            if (Interlocked.Exchange(ref _saidNoBrowser, 1) == 0)
            {
                logger.LogWarning("No browser was started. {Why}", stages.WhyNot);
            }
            else
            {
                logger.LogDebug("No browser was started. {Why}", stages.WhyNot);
            }

            return null;
        }

        // Asked only inside the guard. Read outside it, a stop could land
        // between "it is running" and handing it out, and the answer would be a
        // browser being killed — or, the field read a second time, null, which
        // tells the caller this platform cannot hide a browser at all.
        await _starting.WaitAsync(ct);

        try
        {
            // A cycle and a page render can both arrive at a plugin that has
            // just loaded, and two browsers would each solve every gate.
            if (_process is { IsRunning: true } running)
            {
                return running;
            }

            string executable = await install.EnsureAsync(ct);

            // The stage first, always. Chrome started before it has somewhere
            // to be is a window on the owner's desktop for the half second it
            // takes to move it — which is only visible to somebody sitting at
            // the machine.
            _stage ??= stages.Create();

            // Debug: the browser is how a challenge is cleared, never a step of its own.
            logger.LogDebug(
                "Starting the browser on {Stage}, listening on port {Port}.",
                _stage.Name,
                port);

            IBrowserProcess launched = await _stage.LaunchAsync(executable, Arguments(port), ct);
            _process = launched;

            await ListeningAsync(ct);

            return launched;
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>
    /// What the browser is started with.
    /// </summary>
    /// <remarks>
    /// No headless flag anywhere: measured, headless Chrome does not pass a
    /// managed challenge and every gated source returns the interstitial for
    /// ever. The window is real; it is simply somewhere nobody is looking.
    /// </remarks>
    public static IReadOnlyList<string> Arguments(int port)
    {
        return
        [
            $"--remote-debugging-port={port}",
            "--no-first-run",
            "--no-default-browser-check",
            // A profile of its own, so nothing here touches a browser profile
            // belonging to whoever is logged in.
            "--user-data-dir=profile",
            "--disable-background-networking",
        ];
    }

    /// <summary>
    /// Stops the browser, leaving it able to start again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called when the last tab closes. A browser kept for the life of the
    /// server is a Chrome sitting on a hidden desktop for days between
    /// challenges, holding its profile open and its memory with it — and on a
    /// server that is killed rather than shut down, it is a Chrome that outlives
    /// the plugin. Sixteen of them were found running on the owner's machine
    /// with the server stopped.
    /// </para>
    /// <para>
    /// <see cref="StartAsync"/> builds the stage and the process again, so
    /// stopping costs the next challenge the seconds it takes to start one and
    /// nothing else.
    /// </para>
    /// <para>
    /// <strong>Under the same guard as a start, and after it.</strong> Without
    /// it a stop could land half-way through a start: the desktop closed and
    /// forgotten after it was made, and the start then launching a browser onto
    /// it and handing that out as running — a process with nowhere to be. A stop
    /// asked for during a start now waits for it and takes down what it started.
    /// Awaited rather than blocked on, because a start can take as long as a
    /// Chrome download, and a thread held for that is a thread the server lost.
    /// </para>
    /// </remarks>
    public async Task StopAsync(CancellationToken ct)
    {
        await _starting.WaitAsync(ct);

        try
        {
            StopHeld();
        }
        finally
        {
            _starting.Release();
        }
    }

    /// <summary>Takes the browser and its stage down. Only with <c>_starting</c> held.</summary>
    private void StopHeld()
    {
        // The process before the stage, always: closing the desktop out from
        // under a window that is still on it is the one order that can leave a
        // stray process with nowhere to be.
        _process?.Dispose();
        _process = null;

        _stage?.Dispose();
        _stage = null;
    }

    /// <summary>
    /// Waits until the browser is answering on its debugging port.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A launched process is not a browser that can be spoken to.</strong>
    /// Chrome takes a moment to open its debugging port, and the driver
    /// connecting before it does gets a refused socket — which reads as a
    /// browser that will not start rather than one that is still starting.
    /// </para>
    /// <para>
    /// It was never noticed while the browser was started once and kept, because
    /// whatever happened between launching and the first solve was slower than
    /// Chrome. The moment the browser began closing after every job — the
    /// owner's rule of 11 September 2026, that it runs only while it is needed —
    /// the second job failed on <c>connection refused</c> at 127.0.0.1:9222,
    /// every time.
    /// </para>
    /// <para>
    /// A plain TCP connect, because that is the question being asked: is
    /// anything listening. Asking over HTTP would need a client and would answer
    /// the same thing later.
    /// </para>
    /// </remarks>
    private async Task ListeningAsync(CancellationToken ct)
    {
        if (_listeningWithin <= TimeSpan.Zero)
        {
            return;
        }

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + _listeningWithin;

        while (true)
        {
            try
            {
                using System.Net.Sockets.TcpClient probe = new();

                await probe.ConnectAsync(System.Net.IPAddress.Loopback, port, ct);

                return;
            }
            catch (System.Net.Sockets.SocketException)
            {
                if (DateTimeOffset.UtcNow >= giveUpAt)
                {
                    logger.LogWarning(
                        "The browser did not open its debugging port within {Seconds:0} seconds.",
                        _listeningWithin.TotalSeconds);

                    return;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // Held, for the reason a stop is: a start still launching would
        // otherwise put a browser on a desktop already closed, and then release
        // a guard that no longer exists.
        //
        // For a moment and no longer. A start can be a Chrome download that
        // hangs, and the server's shutdown waits on this: waiting for ever kept
        // the owner's server from stopping. Past that moment what there is is
        // taken down anyway, and the guard is left undisposed for the start
        // still holding it to release.
        bool held = _starting.Wait(DisposeWaits);

        try
        {
            StopHeld();
        }
        finally
        {
            if (held)
            {
                _starting.Release();
                _starting.Dispose();
            }
        }
    }

    /// <summary>How long disposing waits for a start in flight.</summary>
    private static readonly TimeSpan DisposeWaits = TimeSpan.FromSeconds(5);
}
