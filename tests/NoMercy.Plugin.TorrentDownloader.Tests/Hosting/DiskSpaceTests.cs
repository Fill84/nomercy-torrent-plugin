using NoMercy.Plugin.TorrentDownloader.Hosting;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// How much room there is where the downloads go.
/// </summary>
/// <remarks>
/// Against a real disk, because the one thing worth asserting is that it reads
/// the volume a folder is on rather than answering something plausible.
/// </remarks>
public class DiskSpaceTests
{
    [Fact]
    public void AFolderOnARealDiskAnswersWithWhatIsFreeOnIt()
    {
        Assert.True(new DiskSpace().FreeBytes(Path.GetTempPath()) > 0);
    }

    /// <remarks>
    /// Null is not nought. A path on a share that will not say how much is free
    /// is not a share with no room, and refusing every grab on it would be a
    /// plugin that quietly stopped downloading with nothing anywhere saying why.
    /// </remarks>
    [Fact]
    public void APathNothingCanBeToldAboutAnswersUnknownRatherThanNought()
    {
        // A null character is refused by every file system API there is, so
        // this asks the same question of Windows and of Linux. It matters that
        // both are asked: a Linux server roots every absolute path at "/",
        // which is always a real volume, so the UNC share below — the case this
        // rule was written for — cannot be expressed there at all.
        Assert.Null(new DiskSpace().FreeBytes("/downloads/\0/nowhere"));

        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        // The measured case, and the reason the root is matched against the
        // volumes this machine really has: given a UNC path DriveInfo answered
        // with the current drive's free space, so a download folder on a full
        // share was reported as having two hundred gigabytes free.
        Assert.Null(new DiskSpace().FreeBytes(@"\\no-such-host\no-such-share\downloads"));
    }
}
