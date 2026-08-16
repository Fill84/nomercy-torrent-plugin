namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// A torrent the plugin wants downloaded.
/// </summary>
/// <param name="Title">The release name, for the client's own bookkeeping and every page that shows it.</param>
/// <param name="Magnet">Where the torrent starts. Never null: nothing else can be handed over.</param>
/// <param name="Trackers">
/// Every tracker known for it — the union of what each site's magnet named, and
/// the owner's own list. More trackers is a faster download.
/// </param>
public sealed record TorrentRequest(string Title, string Magnet, IReadOnlyList<string> Trackers);

/// <summary>
/// The torrent client, as the pipeline is allowed to see it.
/// </summary>
/// <remarks>
/// Deliberately one method for now. The client itself is Sprint 5 and lives in
/// its own assembly; this port exists so the chain that decides what to
/// download can be finished, and tested, before there is anything to download
/// it with.
/// </remarks>
public interface ITorrentEngine
{
    /// <summary>
    /// Takes on <paramref name="request"/> and answers the info hash it will be
    /// known by.
    /// </summary>
    Task<string> AddAsync(TorrentRequest request, CancellationToken ct);
}
