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

    /// <summary>The libraries the shows in this fake belong to.</summary>
    public Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Library>>(
        [
            .. _shows
                .Select(show => new Library(show.LibraryId, show.Kind.ToString(), show.Kind))
                .DistinctBy(one => one.Id),
        ]);
    }

    public Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<Show>>([.. _shows]);
    }

    /// <summary>Every show whose episodes were asked for, once per asking.</summary>
    /// <remarks>
    /// The anime map is meant to be built from the list the pipeline already
    /// fetched. Fetching a second time would be a call per show per cycle that
    /// nobody would notice until a library with hundreds of shows made the
    /// maintenance pass take minutes.
    /// </remarks>
    public List<int> EpisodesAskedFor { get; } = [];

    public Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        EpisodesAskedFor.Add(showId);

        return Task.FromResult<IReadOnlyList<Episode>>([.. _episodes.GetValueOrDefault(showId, [])]);
    }

    /// <summary>Where a show's episodes already are.</summary>
    public Dictionary<int, List<string>> Files { get; } = [];

}
