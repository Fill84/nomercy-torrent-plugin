using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The episodes the library already holds of the show a torrent added by hand is named for.
/// </summary>
/// <remarks>
/// <para>
/// The owner's rule of 19 November 2026: an episode that is already there is not downloaded again. A torrent
/// added by hand is taken for no episode, so what it must leave out is read here, from the name it gives
/// itself, before the client takes it on: the client chooses its files the moment the metadata arrives and
/// asks nobody then.
/// </para>
/// <para>
/// Null where the name is no show in a library, or the show has no file at all: nothing is left out, and the
/// whole of it is downloaded, as it was before.
/// </para>
/// </remarks>
public static class AlreadyHeld
{
    /// <summary>Reads them for the torrent called <paramref name="name"/>.</summary>
    public static async Task<IReadOnlyList<EpisodeKey>?> OfAsync(string? name, ILibrary library, CancellationToken ct)
    {
        if (Staging.ShowNamed(name, await library.GetShowsAsync(ct)) is not Show show)
        {
            return null;
        }

        EpisodeKey[] held =
        [
            .. (await library.GetEpisodesAsync(show.Id, ct))
                .Where(episode => episode.HasFile)
                .Select(episode => episode.Key),
        ];

        return held.Length > 0 ? held : null;
    }
}
