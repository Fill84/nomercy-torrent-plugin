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
        Assert.Null(new DiskSpace().FreeBytes(@"\\no-such-host\no-such-share\downloads"));
    }
}
