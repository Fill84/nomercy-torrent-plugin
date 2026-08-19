using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// How much room there is on the volume a folder sits on.
/// </summary>
/// <remarks>
/// The one thing the grab needs from the file system, behind the port so that
/// every rule about what to do when there is not enough can be judged without
/// filling a real disk.
/// </remarks>
public sealed class DiskSpace : IStorageSpace
{
    /// <summary>
    /// Bytes free, or null when nothing can say.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Null rather than nought when the volume cannot be read: a share that
    /// will not answer is not a share with no room, and refusing every grab on
    /// it would be a plugin that quietly stopped downloading with nothing
    /// saying why.
    /// </para>
    /// <para>
    /// The root is matched against the volumes this machine really has rather
    /// than handed to <c>DriveInfo</c> to interpret. Given a UNC path
    /// <c>DriveInfo</c> answered with the free space of the current drive — so
    /// a download folder on a full share was reported as having two hundred
    /// gigabytes free, which is the one answer that fills a disk.
    /// </para>
    /// </remarks>
    public long? FreeBytes(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));

            if (string.IsNullOrEmpty(root))
            {
                return null;
            }

            DriveInfo? volume = DriveInfo.GetDrives()
                .FirstOrDefault(drive => string.Equals(drive.Name, root, StringComparison.OrdinalIgnoreCase));

            return volume?.AvailableFreeSpace;
        }
        catch (Exception unknowable) when (unknowable is not OutOfMemoryException)
        {
            return null;
        }
    }
}
