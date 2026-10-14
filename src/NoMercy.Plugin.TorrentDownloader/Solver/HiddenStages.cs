using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// The one place that decides whether a browser can be hidden, and how.
/// </summary>
/// <remarks>
/// <strong>D3.</strong> A browser window opened on the owner's desktop in
/// 0.3.4, which is a fault only visible to somebody sitting at the machine. On
/// macOS there is nowhere to put a window that is not somebody's Space, so
/// gated sources are skipped and the plugin says so — there is no fallback that
/// does not mean a window on a screen, and pretending otherwise is how the
/// window got there.
/// </remarks>
/// <param name="logger">Where the Windows stage says what it is doing.</param>
/// <param name="platform">
/// How this machine hides a window, when a test is saying so. Left out, it is asked of the operating system
/// and of <c>PATH</c> once, when this is made.
/// </param>
public sealed class HiddenStages(ILogger logger, Hiding? platform = null) : IHiddenStageFactory
{
    /// <summary>What the plugin's desktop is called on Windows.</summary>
    public const string DesktopName = "NoMercyTorrentDownloader";

    /// <summary>
    /// What the owner is told on a Linux machine with no <c>Xvfb</c>.
    /// </summary>
    /// <remarks>
    /// GitHub issue 6. The server's Docker images ship neither <c>Xvfb</c> nor a browser, and the plugin chose
    /// the Xvfb stage regardless, so every search that needed the browser failed with ".NET could not start
    /// 'Xvfb' with working directory /app" — a sentence about the plugin's plumbing, not about what to do.
    /// </remarks>
    public const string XvfbMissing = "Xvfb is not installed, so sources behind a challenge are off.";

    /// <summary>
    /// How this machine hides a window, if it can.
    /// </summary>
    /// <remarks>
    /// Decided once. <c>PATH</c> does not change under a running server, and looking through it on every
    /// question would put a file-system scan in the path of every search.
    /// </remarks>
    public Hiding Platform { get; } = platform ?? HidingFor(
        OperatingSystem.IsWindows(),
        OperatingSystem.IsLinux(),
        OperatingSystem.IsLinux() && XvfbLookup.IsOnPath(Environment.GetEnvironmentVariable("PATH")));

    public bool CanHideABrowser => Platform is Hiding.WindowsDesktop or Hiding.XvfbDisplay;

    public string? WhyNot =>
        Platform switch
        {
            Hiding.WindowsDesktop or Hiding.XvfbDisplay => null,
            Hiding.NoXvfb => XvfbMissing,
            _ => "There is nowhere on macOS to put a browser window that is not somebody's Space, so sources behind a challenge are skipped.",
        };

    /// <summary>
    /// The decision itself, separated from asking the operating system what it
    /// is and the file system what is installed.
    /// </summary>
    /// <remarks>
    /// Written this way so the macOS answer, and the Linux answer without
    /// <c>Xvfb</c>, can be asserted on a machine that is neither. A rule about
    /// a platform nobody runs the tests on is otherwise a rule nobody ever
    /// checks — and these are the difference between skipping gated sources
    /// and opening a window on somebody's screen, or starting a program that
    /// is not there.
    /// </remarks>
    public static Hiding HidingFor(bool isWindows, bool isLinux, bool xvfbFound)
    {
        return isWindows ? Hiding.WindowsDesktop
            : isLinux ? (xvfbFound ? Hiding.XvfbDisplay : Hiding.NoXvfb)
            : Hiding.Nowhere;
    }

    public IHiddenStage Create()
    {
        // Both questions are asked: Platform says what this machine can do, and the operating system is asked
        // directly as well so the compiler's platform analyser can see that a Windows-only type is only ever
        // constructed on Windows.
        if (Platform is Hiding.WindowsDesktop && OperatingSystem.IsWindows())
        {
            return new WindowsDesktopStage(DesktopName, logger);
        }

        // Only where Xvfb was found. Linux without it is refused like macOS is: building the stage would
        // start a program that is not there.
        if (Platform is Hiding.XvfbDisplay && OperatingSystem.IsLinux())
        {
            return new XvfbStage();
        }

        throw new PlatformNotSupportedException(WhyNot ?? "This platform cannot hide a browser window.");
    }

    /// <summary>
    /// The port the browser was told to listen on, read back from its own
    /// arguments.
    /// </summary>
    /// <remarks>
    /// Here rather than on either stage: both need it, and neither is available
    /// on the other's platform.
    /// </remarks>
    public static int PortOf(IReadOnlyList<string> arguments)
    {
        const string Flag = "--remote-debugging-port=";

        string? port = arguments.FirstOrDefault(argument => argument.StartsWith(Flag, StringComparison.Ordinal));

        return port is null ? 0 : int.Parse(port[Flag.Length..]);
    }
}

/// <summary>Where a window can be put out of sight.</summary>
public enum Hiding
{
    /// <summary>Nowhere. macOS, and anything else nobody has measured.</summary>
    Nowhere,

    /// <summary>Linux, but no <c>Xvfb</c> on <c>PATH</c>: the server's Docker images.</summary>
    NoXvfb,

    /// <summary>A desktop of its own, via <c>CreateDesktop</c>.</summary>
    WindowsDesktop,

    /// <summary>An <c>Xvfb</c> display, with <c>DISPLAY</c> set for the child only.</summary>
    XvfbDisplay,
}
