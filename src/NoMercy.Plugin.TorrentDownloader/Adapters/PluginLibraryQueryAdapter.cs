// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Adapters;

public sealed class PluginLibraryQueryAdapter(IPluginLibraryQuery library) : ILibraryQuery
{
    // The plugin's subject is episodic television, and the host models anime as its
    // own library type. Both are shows with seasons and episodes here.
    private static readonly string[] ShowLibraryTypes = ["tv", "anime"];

    public async Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct)
    {
        IReadOnlyList<PluginLibrary> libraries = await library.GetLibrariesAsync(ct);
        List<LibraryShow> shows = [];

        foreach (PluginLibrary candidate in libraries)
        {
            if (!ShowLibraryTypes.Contains(candidate.Type, StringComparer.OrdinalIgnoreCase))
                continue;

            IReadOnlyList<PluginLibraryShow> found = await library.GetShowsAsync(candidate.Id, ct);
            shows.AddRange(found.Select(ToShow));
        }

        return shows;
    }

    public async Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        IReadOnlyList<PluginLibraryEpisode> episodes = await library.GetEpisodesAsync(showId, ct);
        return [.. episodes.Select(ToEpisode)];
    }

    public async Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct)
    {
        IReadOnlyList<PluginLibraryFile> files = await library.GetShowFilesAsync(showId, ct);
        return [.. files.Select(ToFile)];
    }

    private static LibraryShow ToShow(PluginLibraryShow show) =>
        new(
            show.Id,
            show.Title,
            show.Year,
            show.LibraryId,
            show.Folder,
            show.EpisodeCount,
            show.HaveEpisodeCount
        )
        {
            Status = ToStatus(show.Status),
        };

    /// <summary>
    /// The host's word for where a show stands, as the core's own.
    ///
    /// <para>
    /// The core assembly holds no reference to the host contract - that is what makes it
    /// testable without a server - so the two enums are separate types and this is the one
    /// place they meet. Written as a switch rather than a cast on the numeric value: they
    /// happen to line up today, and a cast would keep compiling on the day one of them
    /// gains a member in the middle.
    /// </para>
    ///
    /// <para>
    /// Anything unrecognised is <see cref="ShowStatus.Unknown"/>, which counts as still
    /// going out. A newer server naming a status this plugin has not heard of must not
    /// read as "finished" - that is the reading that would stop the plugin working on a
    /// show nobody ended.
    /// </para>
    /// </summary>
    private static ShowStatus ToStatus(PluginShowStatus status) => status switch
    {
        PluginShowStatus.Planned => ShowStatus.Planned,
        PluginShowStatus.InProduction => ShowStatus.InProduction,
        PluginShowStatus.Pilot => ShowStatus.Pilot,
        PluginShowStatus.Returning => ShowStatus.Returning,
        PluginShowStatus.Ended => ShowStatus.Ended,
        PluginShowStatus.Canceled => ShowStatus.Canceled,
        _ => ShowStatus.Unknown,
    };

    private static LibraryEpisode ToEpisode(PluginLibraryEpisode episode) =>
        new(
            episode.ShowId,
            episode.SeasonNumber,
            episode.EpisodeNumber,
            episode.Title,
            ToUtc(episode.AirDate),
            episode.HasFile
        );

    private static LibraryFile ToFile(PluginLibraryFile file) =>
        new(file.ShowId, file.SeasonNumber, file.EpisodeNumber, file.Path, file.Quality);

    // The host's DateTime arrives with Kind Unspecified. Constructing a DateTimeOffset
    // from it directly would apply the server's local offset and shift an air date by
    // up to a day, which is exactly the comparison the daily-show path depends on.
    private static DateTimeOffset? ToUtc(DateTime? value) =>
        value is DateTime date
            ? new DateTimeOffset(DateTime.SpecifyKind(date, DateTimeKind.Utc))
            : null;
}
