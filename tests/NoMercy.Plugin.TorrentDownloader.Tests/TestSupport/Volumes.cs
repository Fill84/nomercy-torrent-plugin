using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>Where on this machine a second file system is.</summary>
public static class Volumes
{
    /// <summary>
    /// Somewhere on a different device from the temporary folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A volume is not a drive letter everywhere. On Windows it is: the drive
    /// this repository is on and the drive the temporary folder is on are two
    /// devices, and comparing path roots finds them. On Linux every absolute
    /// path roots at <c>/</c> however many devices are mounted, so that
    /// comparison finds nothing and the test failed on the first CI build there
    /// had ever been — for want of a second drive letter on a machine with
    /// several file systems.
    /// </para>
    /// <para>
    /// <c>/dev/shm</c> is the one that is always there and always its own:
    /// it is a tmpfs, so a move out of it is a copy exactly as a move between
    /// two disks is.
    /// </para>
    /// <para>
    /// If no second device can be found this says so and fails, rather than
    /// passing quietly. A cross-volume staging that has never run is one nobody
    /// knows about until the owner's server has two disks.
    /// </para>
    /// </remarks>
    public static string Another()
    {
        if (OperatingSystem.IsWindows())
        {
            string here = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))!;
            string temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

            Assert.NotEqual(here, temp);

            return here;
        }

        foreach (string candidate in new[] { "/dev/shm", "/run/shm" })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No second device to stage across: neither /dev/shm nor /run/shm is mounted, and every "
            + "path here roots at /. Cross-volume staging is what turns a move into a copy, and it "
            + "is not being tested on this machine.");
    }
}
