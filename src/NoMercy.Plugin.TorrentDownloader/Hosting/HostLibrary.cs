using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The media server's library, as <see cref="ILibrary"/>.
/// </summary>
/// <remarks>
/// It maps and nothing else. No decision about what is missing, what is worth
/// searching for or what is out of date is taken here — all of that is in Core,
/// where it can be judged without a server. What this does own is the two
/// corrections the contract needs, and both are the kind that only show up
/// against a real library.
/// </remarks>
public sealed class HostLibrary(IPluginLibraryQuery query) : ILibrary
{
    public async Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken ct)
    {
        List<Library> libraries = [];

        foreach (PluginLibrary library in await query.GetLibrariesAsync(ct))
        {
            // The kinds this plugin is for and no others. A music library is a
            // library and is not somewhere an episode goes.
            if (LibraryKinds.TryParse(library.Type, out LibraryKind kind))
            {
                libraries.Add(new(library.Id, library.Title, kind));
            }
        }

        return libraries;
    }

    public async Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct)
    {
        List<Show> shows = [];

        foreach (PluginLibrary library in await query.GetLibrariesAsync(ct))
        {
            if (!LibraryKinds.TryParse(library.Type, out LibraryKind kind))
            {
                continue;
            }

            // Per library id, never null. GetShowsAsync(null) returns every
            // show in every library — it only filters when an id is passed — so
            // asking once for everything would hand back films' shows and the
            // shows of libraries the owner never meant for this, and the plugin
            // would go looking for episodes of them.
            foreach (PluginLibraryShow show in await query.GetShowsAsync(library.Id, ct))
            {
                // No folder is nowhere to download to, so the show is not in
                // scope. Blank counts as none: an empty string is a folder name
                // that resolves to the library root.
                if (string.IsNullOrWhiteSpace(show.Folder))
                {
                    continue;
                }

                shows.Add(new(show.Id, show.Title, show.Year, show.LibraryId, library.Title, kind, show.Folder));
            }
        }

        return shows;
    }


    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        return
        [
            .. (await query.GetEpisodesAsync(showId, ct)).Select(episode => new Episode(
                new(episode.ShowId, episode.SeasonNumber, episode.EpisodeNumber),
                episode.Title,
                // A broadcast day, not a moment. The hours the database carries
                // are not a time anything aired at, and comparing them against
                // "now" would make an episode that aired this morning look as
                // though it had not aired yet.
                episode.AirDate is null ? null : DateOnly.FromDateTime(episode.AirDate.Value),
                episode.HasFile)
            {
                // media-server #35. Nought on a server too old to set it, which
                // the encode gateway refuses rather than guessing around.
                ServerId = episode.Id,
            }),
        ];
    }
}
