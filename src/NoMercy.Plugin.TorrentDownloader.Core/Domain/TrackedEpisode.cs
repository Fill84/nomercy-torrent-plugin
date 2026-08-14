namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// An episode this plugin is keeping an eye on, as derived from the library.
/// </summary>
/// <remarks>
/// Derived, never authoritative. Everything here except <see cref="Attempts"/>
/// and <see cref="LastSearchAt"/> is a copy of what the library said at the
/// last maintenance pass, and is thrown away and rebuilt at the next one. The
/// two exceptions are the plugin's own bookkeeping, which nothing else knows.
/// </remarks>
/// <param name="Key">Which show, season and episode.</param>
/// <param name="ShowTitle">
/// Copied so a page can name the episode without reading the library again, and
/// so the queue still says something when the server is busy.
/// </param>
/// <param name="ShowYear">What makes a show with a common word for a title searchable.</param>
/// <param name="Kind">Television or anime, from the library the show sits in.</param>
/// <param name="EpisodeTitle">The library's title for it, or null when it has none.</param>
/// <param name="AirDate">The broadcast day, or null when none is announced.</param>
/// <param name="State">Where it stands. Derived from the library, not remembered.</param>
/// <param name="Absolute">
/// The episode's number from the start of the series, for anime. Null until
/// S1-03 builds the map, and null for television always.
/// </param>
/// <param name="Attempts">
/// How many times it has been searched for. The plugin's own, preserved across
/// a refresh, and moved only by recording a search — a grab that fails is not a
/// search, and counting it as one is what exhausted episodes in 0.3.4 after
/// three failed downloads.
/// </param>
/// <param name="LastSearchAt">When it was last looked for, which is what orders the queue.</param>
public sealed record TrackedEpisode(
    EpisodeKey Key,
    string ShowTitle,
    int? ShowYear,
    LibraryKind Kind,
    string? EpisodeTitle,
    DateOnly? AirDate,
    EpisodeState State,
    int? Absolute = null,
    int Attempts = 0,
    DateTimeOffset? LastSearchAt = null);
