using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// A path through a symbolic link reads as the path SQLite reports for it.
/// </summary>
/// <remarks>
/// SQLite on Linux and macOS resolves every link in a database's path, so
/// <c>PRAGMA database_list</c> names the real file. The store compares that with
/// where the plugin's data is; compared as written, a data folder under a link
/// (<c>/var</c> on macOS is one) was refused on every open although it was the
/// right file. On Windows SQLite does not resolve links, so the whole failure
/// cannot be shown here — the resolving can.
/// </remarks>
public sealed class RealPathTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void APathThroughALinkedFolderIsThePathOfTheFolderItPointsAt()
    {
        string target = Path.Combine(_folder, "target");
        string link = Path.Combine(_folder, "link");
        Directory.CreateDirectory(target);
        Directory.CreateSymbolicLink(link, target);

        Assert.Equal(
            Path.Combine(target, "data", "torrent-downloader.sqlite"),
            RealPath.Of(Path.Combine(link, "data", "torrent-downloader.sqlite")));
    }

    [Fact]
    public void APathWithNoLinkInItIsItself()
    {
        string plain = Path.Combine(_folder, "plain", "torrent-downloader.sqlite");
        Directory.CreateDirectory(Path.GetDirectoryName(plain)!);

        Assert.Equal(plain, RealPath.Of(plain));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }
}
