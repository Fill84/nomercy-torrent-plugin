// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Library;

public interface ILibraryQuery
{
    Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct);
    Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct);
    Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct);
}

// Folder is nullable because the host's contract makes it nullable. A show with no
// folder cannot be a download target, and the engine must skip it with a reason
// rather than composing a path from null.
public record LibraryShow(
    int ShowId,
    string Title,
    int? Year,
    string LibraryId,
    string? Folder,
    int EpisodeCount,
    int HaveEpisodeCount
);

public record LibraryEpisode(
    int ShowId,
    int SeasonNumber,
    int EpisodeNumber,
    string? Title,
    DateTimeOffset? AirDate,
    bool HasFile
);

public record LibraryFile(
    int ShowId,
    int? SeasonNumber,
    int? EpisodeNumber,
    string Path,
    string Quality
);
