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
public sealed class HiddenStages : IHiddenStageFactory
{
    /// <summary>What the plugin's desktop is called on Windows.</summary>
    public const string DesktopName = "NoMercyTorrentDownloader";

    public bool CanHideABrowser => Platform is Hiding.WindowsDesktop or Hiding.XvfbDisplay;

    public string? WhyNot =>
        CanHideABrowser
            ? null
            : "There is nowhere on macOS to put a browser window that is not somebody's Space, so sources behind a challenge are skipped.";

    /// <summary>How this platform hides a window, if it can.</summary>
    public static Hiding Platform => HidingFor(OperatingSystem.IsWindows(), OperatingSystem.IsLinux());

    /// <summary>
    /// The decision itself, separated from asking the operating system what it
    /// is.
    /// </summary>
    /// <remarks>
    /// Written this way so the macOS answer can be asserted on a machine that
    /// is not a Mac. A rule about a platform nobody runs the tests on is
    /// otherwise a rule nobody ever checks — and this one is the difference
    /// between skipping gated sources and opening a window on somebody's
    /// screen.
    /// </remarks>
    public static Hiding HidingFor(bool isWindows, bool isLinux)
    {
        return isWindows ? Hiding.WindowsDesktop
            : isLinux ? Hiding.XvfbDisplay
            : Hiding.Nowhere;
    }

    public IHiddenStage Create()
    {
        // Asked of the operating system directly rather than of Platform, so
        // the compiler's platform analyser can see that a Windows-only type is
        // only ever constructed on Windows. It is the same question either way,
        // and having it checked is worth the repetition.
        if (OperatingSystem.IsWindows())
        {
            return new WindowsDesktopStage(DesktopName);
        }

        if (OperatingSystem.IsLinux())
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

    /// <summary>A desktop of its own, via <c>CreateDesktop</c>.</summary>
    WindowsDesktop,

    /// <summary>An <c>Xvfb</c> display, with <c>DISPLAY</c> set for the child only.</summary>
    XvfbDisplay,
}
