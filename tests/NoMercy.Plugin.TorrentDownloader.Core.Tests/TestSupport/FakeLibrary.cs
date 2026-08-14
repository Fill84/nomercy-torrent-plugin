using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// A library, in memory, for judging the pipeline without a media server.
/// </summary>
/// <remarks>
/// It holds what <see cref="ILibrary"/> promises and nothing more: shows that
/// are already in scope, and their episodes. Everything the adapter filters out
/// — films, folderless shows, libraries of an unknown type — never reaches
/// this side of the port, so nothing here re-tests it.
/// </remarks>
public sealed class FakeLibrary : ILibrary
{
    private readonly List<Show> _shows = [];
    private readonly Dictionary<int, List<Episode>> _episodes = [];

    public FakeLibrary Show(
        int id,
        string title,
        int? year = null,
        LibraryKind kind = LibraryKind.Television,
        string libraryId = "lib-tv")
    {
        _shows.Add(new(id, title, year, libraryId, kind, title));
        return this;
    }

    public FakeLibrary Episode(
        int showId,
        int season,
        int number,
        DateOnly? airDate = null,
        bool hasFile = false,
        string? title = "An episode")
    {
        if (!_episodes.TryGetValue(showId, out List<Episode>? list))
        {
            list = [];
            _episodes[showId] = list;
        }

        list.Add(new(new(showId, season, number), title, airDate, hasFile));
        return this;
    }

    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Show>>([.. _shows]);
    }

    public Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Episode>>([.. _episodes.GetValueOrDefault(showId, [])]);
    }
}
