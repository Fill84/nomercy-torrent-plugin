namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>A browser this plugin started, and whether it is still alive.</summary>
public interface IBrowserProcess : IDisposable
{
    bool IsRunning { get; }

    /// <summary>The port its remote-debugging endpoint is on.</summary>
    int Port { get; }
}

/// <summary>
/// Somewhere to put a window that is not anybody's screen.
/// </summary>
/// <remarks>
/// <para>
/// The stage launches the browser rather than merely existing beside it,
/// because only the stage knows how a child process is told to appear on it: a
/// Windows desktop is chosen through a field of <c>STARTUPINFO</c> that
/// <c>Process.Start</c> cannot reach, and an X display is chosen through an
/// environment variable. Splitting "make the stage" from "put the browser on
/// it" is what lets the second be forgotten.
/// </para>
/// <para>
/// <strong>D3.</strong> The stage exists before Chrome does. Starting Chrome
/// first and moving it afterwards puts a window on the owner's desktop for the
/// half second in between — which is only visible to somebody sitting at the
/// machine, and so was invisible to everybody who was not.
/// </para>
/// </remarks>
public interface IHiddenStage : IDisposable
{
    /// <summary>What it is, for the journal: a desktop name or a display number.</summary>
    string Name { get; }

    /// <summary>Starts <paramref name="executable"/> on this stage.</summary>
    Task<IBrowserProcess> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct);
}

/// <summary>Making the stage this platform can hide a window on.</summary>
public interface IHiddenStageFactory
{
    /// <summary>
    /// Whether a window can be hidden here at all.
    /// </summary>
    /// <remarks>
    /// The one place that decides. Anything else asking the question a second
    /// way is a second answer waiting to disagree.
    /// </remarks>
    bool CanHideABrowser { get; }

    /// <summary>Why not, when it cannot. Null when it can.</summary>
    string? WhyNot { get; }

    /// <summary>
    /// A stage, ready before anything is launched on it.
    /// </summary>
    /// <exception cref="PlatformNotSupportedException">
    /// When <see cref="CanHideABrowser"/> is false. Refusing is the point:
    /// there is nothing to fall back to that does not put a window on somebody's
    /// screen.
    /// </exception>
    IHiddenStage Create();
}
