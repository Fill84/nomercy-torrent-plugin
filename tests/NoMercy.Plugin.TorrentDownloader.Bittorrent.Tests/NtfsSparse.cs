using System.Runtime.InteropServices;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Marks a file sparse the way the media server's host does for a plugin
/// (<c>IPluginStorage.MarkSparseAsync</c>, media-server #609): on Windows only,
/// on a file that exists, opened for its attributes alone so the writer's own
/// handle may stay open, and best effort.
/// </summary>
/// <remarks>
/// Test code, so the native call is allowed here: it is what the host does on
/// the plugin's behalf, and the plugin itself ships none (media-server #476).
/// </remarks>
internal static class NtfsSparse
{
    private const int FsctlSetSparse = 0x000900C4;

    private const uint FileWriteAttributes = 0x0100;

    public static bool Mark(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path))
        {
            return false;
        }

        using Microsoft.Win32.SafeHandles.SafeFileHandle handle = CreateFile(
            path,
            FileWriteAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            nint.Zero,
            FileMode.Open,
            0,
            nint.Zero);

        return !handle.IsInvalid && DeviceIoControl(handle, FsctlSetSparse, nint.Zero, 0, nint.Zero, 0, out _, nint.Zero);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(
        string name,
        uint access,
        FileShare share,
        nint security,
        FileMode mode,
        uint flags,
        nint template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        int code,
        nint input,
        int inputLength,
        nint output,
        int outputLength,
        out int returned,
        nint overlapped);
}
