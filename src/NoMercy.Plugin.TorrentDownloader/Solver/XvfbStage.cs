using System.Diagnostics;
using System.Runtime.Versioning;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// An X display of the plugin's own, served by <c>Xvfb</c>, with the browser
/// launched onto it.
/// </summary>
/// <remarks>
/// <c>DISPLAY</c> is set on the child's environment and never on this process's.
/// Setting it here would move anything else this server later starts onto the
/// same invisible display, and a plugin has no business changing the
/// environment of the process that loaded it.
/// </remarks>
[SupportedOSPlatform("linux")]
public sealed class XvfbStage : IHiddenStage
{
    /// <summary>
    /// A display number well clear of a real session's.
    /// </summary>
    /// <remarks>
    /// A desktop session owns <c>:0</c>, and a second one <c>:1</c>. Starting
    /// at ninety-nine keeps this out of the way of anything a person is
    /// actually looking at.
    /// </remarks>
    public const int FirstDisplay = 99;

    private readonly Process _xvfb;

    public XvfbStage(int display = FirstDisplay)
    {
        Name = $":{display}";

        // Started before the browser, and its own window server: the browser
        // has somewhere to open on the moment it exists, rather than a moment
        // afterwards.
        _xvfb = Process.Start(new ProcessStartInfo("Xvfb")
        {
            ArgumentList = { Name, "-screen", "0", "1920x1080x24", "-nolisten", "tcp" },
            UseShellExecute = false,
            RedirectStandardError = true,
        }) ?? throw new InvalidOperationException($"Xvfb did not start on {Name}.");
    }

    public string Name { get; }

    public Task<IBrowserProcess> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        ProcessStartInfo start = new(executable) { UseShellExecute = false };

        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // On the child only.
        start.Environment["DISPLAY"] = Name;

        Process browser = Process.Start(start)
                          ?? throw new InvalidOperationException($"The browser did not start on {Name}.");

        return Task.FromResult<IBrowserProcess>(
            new StartedBrowser(browser.Id, HiddenStages.PortOf(arguments)));
    }

    public void Dispose()
    {
        try
        {
            if (!_xvfb.HasExited)
            {
                _xvfb.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }
        finally
        {
            _xvfb.Dispose();
        }
    }
}
