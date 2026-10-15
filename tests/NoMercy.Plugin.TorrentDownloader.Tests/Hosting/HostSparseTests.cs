using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// A new download's files are made sparse by the host, not by a native call in
/// the plugin (media-server #476, #609).
/// </summary>
public sealed class HostSparseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public void AFileIsMadeSparseThroughTheHost()
    {
        // NTFS is the only file system where this means anything: every other
        // one the plugin runs on is sparse by default, and the host answers
        // false there.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string file = Path.Combine(_folder, "episode.mkv");
        Directory.CreateDirectory(_folder);

        // Held open for writing, as the disk holds it when it asks.
        using FileStream writing = new(file, FileMode.CreateNew, FileAccess.Write, FileShare.ReadWrite);

        Assert.True(HostSparse.Through(() => new HostShapedStorage(_folder))(file));
        Assert.True(File.GetAttributes(file).HasFlag(FileAttributes.SparseFile));
    }

    [Fact]
    public void AHostThatRefusesLeavesTheDownloadGoing()
    {
        // A host that wired no storage refuses the facade by name. A download
        // must not fail over a flag it can do without.
        Func<string, bool> mark = HostSparse.Through(() => throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost("test", "IPluginContext.Storage")));

        Assert.False(mark(Path.Combine(_folder, "episode.mkv")));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }
}
