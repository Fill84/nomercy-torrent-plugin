// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IPluginLibraryQuery. Shows are keyed by library id so a test
// can assert the adapter asks each library for its own shows rather than fetching
// everything once and filtering client-side.
public sealed class FakeLibraryQuery : IPluginLibraryQuery
{
    public List<PluginLibrary> Libraries { get; set; } = [];
    public Dictionary<string, List<PluginLibraryShow>> ShowsByLibraryId { get; set; } = [];
    public List<PluginLibraryEpisode> Episodes { get; set; } = [];
    public List<PluginLibraryFile> Files { get; set; } = [];

    public int GetLibrariesCallCount { get; private set; }
    public int GetShowsCallCount { get; private set; }
    public int GetEpisodesCallCount { get; private set; }
    public int GetShowFilesCallCount { get; private set; }

    public Task<IReadOnlyList<PluginLibrary>> GetLibrariesAsync(CancellationToken ct = default)
    {
        GetLibrariesCallCount++;
        return Task.FromResult<IReadOnlyList<PluginLibrary>>(Libraries);
    }

    public Task<IReadOnlyList<PluginLibraryShow>> GetShowsAsync(
        string? libraryId = null,
        CancellationToken ct = default
    )
    {
        GetShowsCallCount++;

        // No library id means every show, which is what the contract says: "Shows,
        // optionally narrowed to one library". Returning nothing for the un-narrowed
        // call made this double disagree with the thing it stands in for, and a caller
        // that asked the honest way got an empty library.
        List<PluginLibraryShow> shows = libraryId is null
            ? [.. ShowsByLibraryId.Values.SelectMany(entry => entry)]
            : ShowsByLibraryId.TryGetValue(libraryId, out List<PluginLibraryShow>? found)
                ? found
                : [];

        return Task.FromResult<IReadOnlyList<PluginLibraryShow>>(shows);
    }

    public Task<IReadOnlyList<PluginLibraryMovie>> GetMoviesAsync(
        string? libraryId = null,
        CancellationToken ct = default
    )
    {
        return Task.FromResult<IReadOnlyList<PluginLibraryMovie>>([]);
    }

    public Task<IReadOnlyList<PluginLibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct = default)
    {
        GetEpisodesCallCount++;
        return Task.FromResult<IReadOnlyList<PluginLibraryEpisode>>(
            [.. Episodes.Where(episode => episode.ShowId == showId)]
        );
    }

    public Task<IReadOnlyList<PluginLibraryFile>> GetShowFilesAsync(int showId, CancellationToken ct = default)
    {
        GetShowFilesCallCount++;
        return Task.FromResult<IReadOnlyList<PluginLibraryFile>>([.. Files.Where(file => file.ShowId == showId)]);
    }
}
