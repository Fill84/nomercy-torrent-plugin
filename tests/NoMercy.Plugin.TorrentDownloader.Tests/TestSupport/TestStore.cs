using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>The plugin's store in a folder, with the database the host would open there.</summary>
public static class TestStore
{
    public static Store In(string folder)
    {
        return new Store(folder, () => new HostShapedStorage(folder));
    }
}
