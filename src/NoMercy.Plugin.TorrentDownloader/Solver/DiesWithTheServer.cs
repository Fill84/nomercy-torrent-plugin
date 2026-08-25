using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// A Windows job object holding the browser, so that it dies with this process
/// however this process ends.
/// </summary>
/// <remarks>
/// <para>
/// Every other tidy-up here runs on the way out: the last tab closing stops the
/// browser, and disposing the plugin stops it too. Both need a shutdown to
/// happen. A server that is killed — task manager, a restart that does not
/// wait, a crash — runs neither, and the browser carries on with nobody left to
/// close it. Sixteen were found running on the owner's machine with the server
/// stopped.
/// </para>
/// <para>
/// A job object is the only arrangement that survives that, because the kernel
/// enforces it rather than the process. When the last handle to the job closes
/// — which the kernel does on the way out however the process ended — every
/// process in it is terminated. Chrome's own children join the job with it, so
/// the renderers go too.
/// </para>
/// </remarks>
public sealed class DiesWithTheServer : IDisposable
{
    private nint _job;

    private DiesWithTheServer(nint job)
    {
        _job = job;
    }

    /// <summary>
    /// The job, or null where there is nothing to arrange.
    /// </summary>
    /// <returns>
    /// Null off Windows: a job object is a Windows idea, and the Linux stage
    /// runs the browser as a child of the server, which already dies with it.
    /// Null is not a failure — the caller launches either way.
    /// </returns>
    public static DiesWithTheServer? Create(ILogger logger)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        nint job = CreateJobObject(nint.Zero, null);

        if (job == nint.Zero)
        {
            // Said, not thrown. A plugin that cannot make a job object can
            // still solve every challenge it is asked to; what it loses is the
            // guarantee about being killed, and that is worth a line rather
            // than a dead solver.
            logger.LogWarning(
                "The browser could not be put in a job object, so one may outlive a server that is killed. {Why}",
                new Win32Exception(Marshal.GetLastWin32Error()).Message);

            return null;
        }

        ExtendedLimitInformation limits = new()
        {
            BasicLimitInformation = new BasicLimitInformation
            {
                LimitFlags = KillOnJobClose,
            },
        };

        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        nint block = Marshal.AllocHGlobal(size);

        try
        {
            Marshal.StructureToPtr(limits, block, fDeleteOld: false);

            if (!SetInformationJobObject(job, ExtendedLimitInformationClass, block, size))
            {
                logger.LogWarning(
                    "The browser's job object would not take the kill-on-close limit, so one may outlive a server that is killed. {Why}",
                    new Win32Exception(Marshal.GetLastWin32Error()).Message);

                CloseHandle(job);

                return null;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }

        return new DiesWithTheServer(job);
    }

    /// <summary>Puts a process in the job.</summary>
    /// <param name="process">
    /// An open handle to it. The job holds the process itself rather than the
    /// handle, so the caller is free to close it afterwards.
    /// </param>
    public void Take(nint process)
    {
        ObjectDisposedException.ThrowIf(_job == nint.Zero, this);

        if (!AssignProcessToJobObject(_job, process))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "The browser could not be put in the job object that kills it with the server.");
        }
    }

    /// <remarks>
    /// Closing the handle is what kills what is in it, so this is the ordinary
    /// shutdown as well as the backstop. Nothing else has to be arranged for
    /// the killed case: the kernel closes this handle for us.
    /// </remarks>
    public void Dispose()
    {
        if (_job == nint.Zero)
        {
            return;
        }

        CloseHandle(_job);

        _job = nint.Zero;
    }

    /// <summary>Terminate everything in the job when its last handle closes.</summary>
    private const uint KillOnJobClose = 0x2000;

    /// <summary>JobObjectExtendedLimitInformation.</summary>
    private const int ExtendedLimitInformationClass = 9;

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateJobObject(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, int length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    /// <summary>
    /// The whole structure, laid out as Windows expects it.
    /// </summary>
    /// <remarks>
    /// The counters and the memory limits are never read or set here, but the
    /// call takes the size of the whole thing and rejects anything shorter, so
    /// every field has to be present and in order.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
