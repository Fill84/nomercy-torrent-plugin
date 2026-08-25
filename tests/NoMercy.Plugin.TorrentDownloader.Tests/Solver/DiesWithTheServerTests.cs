using System.Diagnostics;

using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// A browser this plugin started does not outlive the server that started it.
/// </summary>
/// <remarks>
/// <para>
/// Every tidy-up this plugin has runs on the way out: the last tab closing
/// stops the browser, and disposing the plugin stops it too. Both need the
/// server to shut down. A server that is <em>killed</em> — task manager, a
/// restart that does not wait, a crash — runs neither, and Chrome carries on
/// with nobody left to close it.
/// </para>
/// <para>
/// It is not theoretical. Sixteen of them were found running on the owner's
/// machine with the server stopped, each holding its profile and its memory.
/// </para>
/// <para>
/// A job object is the only thing that survives being killed, because the
/// kernel enforces it rather than the process: when the last handle to the job
/// closes — which happens however the process ends — everything in it is
/// terminated.
/// </para>
/// </remarks>
public class DiesWithTheServerTests
{
    [Fact]
    public void WhatIsInTheJobDiesWhenTheJobCloses()
    {
        if (!OperatingSystem.IsWindows())
        {
            // A job object is a Windows idea. The Linux stage is Xvfb, whose
            // browser is a child of the server and already dies with it, so
            // there is nothing here to arrange on that platform.
            Assert.Null(DiesWithTheServer.Create(new CapturingLogger()));

            return;
        }

        // Something that would happily run for a minute if nothing stopped it,
        // so that it exiting is the job doing its work rather than the command
        // finishing on its own.
        using Process child = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 60 127.0.0.1")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
        })!;

        try
        {
            DiesWithTheServer? job = DiesWithTheServer.Create(new CapturingLogger());

            Assert.NotNull(job);

            job.Take(child.Handle);

            // Still running: what follows has to be the close doing it.
            Assert.False(child.HasExited);

            job.Dispose();

            Assert.True(
                child.WaitForExit(TimeSpan.FromSeconds(10)),
                "The process was still running after the job that held it was closed.");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill(entireProcessTree: true);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// The launch itself, because putting a process in a job means starting it
    /// suspended and resuming it afterwards, and a browser that is never
    /// resumed is a solver that hangs on every gated source rather than an
    /// orphan nobody noticed.
    /// </para>
    /// <para>
    /// So this asserts both halves: that what the stage launched really ran,
    /// and that closing the stage took it with it. A test that only checked the
    /// second would pass on a browser that never started.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatTheStageLaunchedRunsAndGoesWithIt()
    {
        if (!OperatingSystem.IsWindows())
        {
            // The hidden desktop is a Windows idea; Linux hides a browser on an
            // Xvfb display, which is a different class and a different test.
            return;
        }

        string marker = Path.Combine(
            Path.GetTempPath(),
            "nomercy-stage-" + Guid.NewGuid().ToString("n")[..8] + ".txt");

        // A stage of its own, so that a plugin running on this machine keeps
        // its desktop and its browser.
        WindowsDesktopStage stage = new(
            "NoMercyTorrentDownloaderTest" + Guid.NewGuid().ToString("n")[..8],
            new CapturingLogger());

        IBrowserProcess started;

        try
        {
            // It writes the file first and then stays up for a minute, so the
            // file appearing says it was resumed and it still being there says
            // it was not the command simply finishing.
            started = await stage.LaunchAsync(
                Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe",
                ["/c", $"echo ran> \"{marker}\" & ping -n 60 127.0.0.1"],
                CancellationToken.None);
        }
        catch
        {
            stage.Dispose();

            throw;
        }

        try
        {
            Assert.True(
                await Eventually(() => File.Exists(marker)),
                "Nothing was written, so what the stage launched never ran: it was left suspended.");

            Assert.True(started.IsRunning);

            stage.Dispose();

            Assert.True(
                await Eventually(() => !started.IsRunning),
                "The process was still running after the stage that launched it was closed.");
        }
        finally
        {
            stage.Dispose();
            started.Dispose();

            // Best effort: the file is in the machine's temp folder and a
            // failure to remove it is not a failure of this test.
            try
            {
                File.Delete(marker);
            }
            catch (IOException)
            {
                // Still held by something on its way out. Temp is temp.
            }
        }
    }

    /// <summary>Waits up to ten seconds for something to become true.</summary>
    /// <remarks>
    /// Polled rather than slept: a process starting and a process being
    /// terminated are both usually immediate and occasionally not, and a fixed
    /// wait is either slow every time or flaky once in a while.
    /// </remarks>
    private static async Task<bool> Eventually(Func<bool> what)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (what())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return what();
    }
}
