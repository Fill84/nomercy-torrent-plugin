using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// A Windows desktop of the plugin's own, with the browser launched onto it.
/// </summary>
/// <remarks>
/// <para>
/// A desktop is the only place on Windows to put a window where nobody can see
/// it. A window on it is real, has focus, and passes a managed challenge — it
/// is simply not on the desktop anybody is looking at.
/// </para>
/// <para>
/// Launching goes through <c>CreateProcess</c> rather than
/// <c>Process.Start</c>, because the desktop is chosen through
/// <c>STARTUPINFO.lpDesktop</c> and .NET's process API has no way to set it.
/// Starting the browser normally and moving it afterwards is the fault this
/// exists to avoid: the window is on the owner's screen for the half second in
/// between.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WindowsDesktopStage : IHiddenStage
{
    private const uint GenericAll = 0x10000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;

    /// <summary>
    /// Started stopped, so that nothing has run before it is in the job.
    /// </summary>
    /// <remarks>
    /// Chrome spawns its renderers within milliseconds of starting. A process
    /// put in a job after it has already forked leaves those children outside
    /// it, and they are the ones that hold the memory. Suspended, assigned,
    /// then resumed, so there is no window in which that can happen.
    /// </remarks>
    private const uint CreateSuspended = 0x00000004;

    private readonly nint _desktop;
    private readonly DiesWithTheServer? _job;
    private bool _closed;

    public WindowsDesktopStage(string name, ILogger logger)
    {
        Name = name;

        // Before the desktop, because it costs nothing and there is no order
        // dependency: a stage that cannot make one still launches.
        _job = DiesWithTheServer.Create(logger);

        // Created here, before anything is launched. That ordering is the whole
        // point of the class.
        _desktop = CreateDesktop(name, nint.Zero, nint.Zero, 0, GenericAll, nint.Zero);

        if (_desktop == nint.Zero)
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not create the hidden desktop '{name}'.");
        }
    }

    public string Name { get; }

    public Task<IBrowserProcess> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        // CreateProcess writes into its command line, so it cannot be a literal.
        char[] commandLine = [.. $"\"{executable}\" {string.Join(' ', arguments)}", '\0'];

        StartupInfo startup = new()
        {
            Cb = Marshal.SizeOf<StartupInfo>(),
            LpDesktop = Name,
        };

        if (!CreateProcess(
                null,
                commandLine,
                nint.Zero,
                nint.Zero,
                bInheritHandles: false,
                CreateUnicodeEnvironment | CreateSuspended,
                nint.Zero,
                null,
                ref startup,
                out ProcessInformation created))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Could not start the browser on the hidden desktop '{Name}'.");
        }

        try
        {
            // While it is still suspended. The job holds the process rather
            // than this handle, so closing the handle below changes nothing.
            _job?.Take(created.HProcess);
        }
        catch (Win32Exception)
        {
            // The browser is suspended and nothing has run in it, so this is
            // the one moment it can be taken back cleanly. Leaving it running
            // outside the job would be the orphan this class exists to prevent.
            TerminateProcess(created.HProcess, 1);
            CloseHandle(created.HThread);
            CloseHandle(created.HProcess);

            throw;
        }

        if (ResumeThread(created.HThread) == -1)
        {
            int why = Marshal.GetLastWin32Error();

            TerminateProcess(created.HProcess, 1);
            CloseHandle(created.HThread);
            CloseHandle(created.HProcess);

            throw new Win32Exception(why, "The browser was started but could not be resumed.");
        }

        // The handles are the plugin's now. The process is tracked through the
        // ordinary API from here; these two only had to survive the call.
        CloseHandle(created.HThread);
        CloseHandle(created.HProcess);

        return Task.FromResult<IBrowserProcess>(new StartedBrowser(created.DwProcessId, HiddenStages.PortOf(arguments)));
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;

        // The job before the desktop: closing it is what stops the browser, and
        // the desktop cannot close while a window is still on it.
        _job?.Dispose();

        CloseDesktop(_desktop);
    }

    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateDesktop(
        string lpszDesktop,
        nint lpszDevice,
        nint pDevmode,
        int dwFlags,
        uint dwDesiredAccess,
        nint lpsa);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(nint hDesktop);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern int ResumeThread(nint thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        char[] lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        nint lpEnvironment,
        string? lpCurrentDirectory,
        ref StartupInfo lpStartupInfo,
        out ProcessInformation lpProcessInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Cb;
        public string? LpReserved;

        /// <summary>The whole reason this class exists.</summary>
        public string? LpDesktop;

        public string? LpTitle;
        public int DwX;
        public int DwY;
        public int DwXSize;
        public int DwYSize;
        public int DwXCountChars;
        public int DwYCountChars;
        public int DwFillAttribute;
        public int DwFlags;
        public short WShowWindow;
        public short CbReserved2;
        public nint LpReserved2;
        public nint HStdInput;
        public nint HStdOutput;
        public nint HStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint HProcess;
        public nint HThread;
        public int DwProcessId;
        public int DwThreadId;
    }
}

/// <summary>A browser started by this plugin, tracked by its process id.</summary>
internal sealed class StartedBrowser(int processId, int port) : IBrowserProcess
{
    private Process? _process = SafeGet(processId);

    public bool IsRunning
    {
        get
        {
            // Refreshed rather than cached: a browser that died five minutes ago
            // is not one to hand a page to, and "we started it" is not evidence
            // that it is still there.
            _process?.Refresh();

            return _process is { HasExited: false };
        }
    }

    public int Port { get; } = port;

    public void Dispose()
    {
        try
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, which is the outcome asked for.
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private static Process? SafeGet(int processId)
    {
        try
        {
            return Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // Started and exited before we could look it up. Rare, and not
            // worth throwing over: IsRunning answers false, which is true.
            return null;
        }
    }
}
