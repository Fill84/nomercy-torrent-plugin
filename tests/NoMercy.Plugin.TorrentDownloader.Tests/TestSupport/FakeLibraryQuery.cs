using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The server's library, as far as a test is concerned — including the parts of
/// its behaviour that caught 0.3.4 out.
/// </summary>
/// <remarks>
/// <see cref="GetShowsAsync"/> returns <em>every show in every library</em> when
/// it is given no library id, exactly as the real one does. That is what makes
/// the test about films an outcome rather than an inspection: an adapter that
/// asks for everything at once gets everything, and a film's show turns up in
/// the answer on its own.
/// </remarks>
public sealed class FakeLibraryQuery : IPluginLibraryQuery
{
    private readonly List<PluginLibrary> _libraries = [];
    private readonly List<PluginLibraryShow> _shows = [];
    private readonly Dictionary<int, List<PluginLibraryEpisode>> _episodes = [];

    /// <summary>Every library id the adapter asked about; null when it asked for all of them.</summary>
    public List<string?> Asked { get; } = [];

    public FakeLibraryQuery Library(string id, string title, string type)
    {
        _libraries.Add(new(id, title, type));
        return this;
    }

    /// <summary>
    /// Adds a show. <c>haveEpisodeCount</c> is the column that lies: nought
    /// here with episodes on disk is not a contrivance, it is what a real
    /// server returns.
    /// </summary>
    public FakeLibraryQuery Show(
        int id,
        string title,
        string libraryId,
        int? year = null,
        string? folder = "Some Show",
        int episodeCount = 0,
        int haveEpisodeCount = 0)
    {
        _shows.Add(new(id, title, year, libraryId, folder, episodeCount, haveEpisodeCount));
        return this;
    }

    public FakeLibraryQuery Episode(
        int showId,
        int season,
        int number,
        string? title = "An episode",
        DateTime? airDate = null,
        bool hasFile = false)
    {
        if (!_episodes.TryGetValue(showId, out List<PluginLibraryEpisode>? list))
        {
            list = [];
            _episodes[showId] = list;
        }

        list.Add(new(showId, season, number, title, airDate, hasFile));
        return this;
    }

    public Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginLibrary>>([.. _libraries]);
    }

    public Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default)
    {
        Asked.Add(libraryId);

        return Task.FromResult<IReadOnlyList<PluginLibraryShow>>(
            [.. _shows.Where(show => libraryId is null || show.LibraryId == libraryId)]);
    }

    public Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(
        int showId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginLibraryEpisode>>(
            [.. _episodes.GetValueOrDefault(showId, [])]);
    }

    public Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default)
    {
        // Films are out of scope, so this is never called. Throwing says so
        // where returning an empty list would hide the day something did.
        throw new NotSupportedException("Films are out of scope; GetMoviesAsync is never called.");
    }

    private readonly List<PluginLibraryFile> _files = [];

    /// <summary>One file a show already has, with the folder it really lives in.</summary>
    public FakeLibraryQuery File(int showId, int season, int episode, string path)
    {
        _files.Add(new(showId, season, episode, path, "1080p"));

        return this;
    }

    public Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(
        int showId,
        CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginLibraryFile>>(
            [.. _files.Where(one => one.ShowId == showId)]);
    }
}
