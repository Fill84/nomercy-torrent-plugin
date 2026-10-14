using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>The torrent client's lines, into the plugin's log at the owner's level.</summary>
/// <remarks>
/// The client cannot take an <see cref="ILogger"/>: its project references nothing, so its tests can run
/// on captured wire bytes with no host in sight. This is the one place the two meet. Debugging and Tracing
/// are asked of the plugin's log on every line, so the owner's level reaches the peer code the moment it is
/// saved.
/// </remarks>
public sealed class ClientLog(ILogger log) : IEngineLog
{
    public bool Debugging => log.IsEnabled(LogLevel.Debug);

    public bool Tracing => log.IsEnabled(LogLevel.Trace);

    public void Debug(string line)
    {
        log.LogDebug("{Line}", line);
    }

    public void Trace(string line)
    {
        log.LogTrace("{Line}", line);
    }
}
