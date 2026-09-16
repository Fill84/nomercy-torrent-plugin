using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>A disk with as much room as the test says, or as much as anyone could want.</summary>
public sealed class EndlessDisk(long? free) : IStorageSpace
{
    public long? FreeBytes(string folder)
    {
        return free ?? long.MaxValue;
    }
}
