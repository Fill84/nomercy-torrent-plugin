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
)
{
    /// <summary>
    /// Whether the show is still going out, as the library says.
    ///
    /// <para>
    /// Asked for rather than worked out. This used to be derived here from air dates -
    /// something aired lately, so it must still be running - and on a real library that
    /// read a series cancelled last month as current and a show on a nine-month hiatus as
    /// finished. The server knows; a plugin guessing is a plugin spending somebody's
    /// bandwidth on a season that will never be made.
    /// </para>
    /// </summary>
    public ShowStatus Status { get; init; } = ShowStatus.Unknown;
}

/// <summary>
/// Where a show is in its life.
///
/// <para>
/// This core assembly deliberately owns no reference to the host contract - it is the part
/// of the plugin that can be tested without a server - so this mirrors
/// <c>PluginShowStatus</c> rather than reusing it. The adapter in the shell project is the
/// one place the two meet.
/// </para>
/// </summary>
public enum ShowStatus
{
    /// <summary>
    /// The library does not say, or says something this plugin does not recognise.
    ///
    /// <para>
    /// Counts as still going out everywhere it is asked. A server too old to answer the
    /// question reports this for every show, and the plugin carrying on as it always did
    /// is the right failure - stopping work on a whole library because the contract grew a
    /// field is not.
    /// </para>
    /// </summary>
    Unknown = 0,

    Planned = 1,
    InProduction = 2,
    Pilot = 3,
    Returning = 4,
    Ended = 5,
    Canceled = 6,
}

public static class ShowStatusExtensions
{
    /// <summary>
    /// Whether more of it is ever coming.
    ///
    /// <para>
    /// The one question the plugin actually has. Everything that is not finished counts,
    /// including <see cref="ShowStatus.Unknown"/> - see there for why the doubt falls that
    /// way.
    /// </para>
    /// </summary>
    public static bool StillGoing(this ShowStatus status) =>
        status is not (ShowStatus.Ended or ShowStatus.Canceled);
}

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
