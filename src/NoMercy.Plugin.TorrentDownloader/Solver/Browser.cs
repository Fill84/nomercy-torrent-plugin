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
            // Said once per attempt at debug level and once loudly by whoever
            // skips the sources; starting nothing is the behaviour that matters.
            logger.LogWarning("No browser was started. {Why}", stages.WhyNot);

            return null;
        }

        if (_process is { IsRunning: true })
        {
            return _process;
        }

        await _starting.WaitAsync(ct);

        try
        {
            // Checked again inside: a cycle and a page render can both
            // arrive at a plugin that has just loaded, and two browsers would
            // each solve every gate.
            if (_process is { IsRunning: true })
            {
                return _process;
            }

            string executable = await install.EnsureAsync(ct);

            // The stage first, always. Chrome started before it has somewhere
            // to be is a window on the owner's desktop for the half second it
            // takes to move it — which is only visible to somebody sitting at
            // the machine.
            _stage ??= stages.Create();

            logger.LogInformation(
                "Starting the browser on {Stage}, listening on port {Port}.",
                _stage.Name,
                port);

            _process = await _stage.LaunchAsync(executable, Arguments(port), ct);

            await ListeningAsync(ct);

            return _process;
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
    /// </remarks>
    public void Stop()
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

        Stop();

        _starting.Dispose();
    }
}
