using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The host's call that makes a new file sparse, in the shape the torrent
/// client's disk asks for it.
/// </summary>
/// <remarks>
/// <para>
/// The disk made the native call itself, and that is the one the marketplace
/// scan refuses (media-server #476). The host has it now:
/// <c>IPluginStorage.MarkSparseAsync(localPath)</c>, ABI 12.4 (#609), for a file
/// at a real local path — the incomplete folder is one, and cannot be a scope.
/// </para>
/// <para>
/// Waited on rather than handed over as a task: the disk makes a torrent's files
/// on the client's own thread, one after another, and the host's call is a
/// single attribute change underneath. Making the disk asynchronous for it would
/// change every caller to save nothing.
/// </para>
/// </remarks>
public static class HostSparse
{
    public static Func<string, bool> Through(Func<IPluginStorage> storage)
    {
        return path =>
        {
            try
            {
                return storage().MarkSparseAsync(path).GetAwaiter().GetResult();
            }
            catch (PluginRefusedException)
            {
                // A host that wired no storage. The download goes on at full
                // size, which is what a file system that refuses the flag gets
                // too: worse than sparse, better than no download.
                return false;
            }
        };
    }
}
