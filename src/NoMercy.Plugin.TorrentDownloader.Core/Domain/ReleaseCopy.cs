namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// One copy of one release, on one site.
/// </summary>
/// <remarks>
/// A copy is what a name becomes once an indexer has answered for it. The
/// difference matters more than it sounds: a name has no seeders, no size and
/// no site, and 0.3.4 asked one how many seeders it had, got nought, and
/// refused every announcement it ever saw.
/// </remarks>
/// <param name="Title">The release name this copy is of, as the site printed it.</param>
/// <param name="Source">Which site answered with it, for the history line and the ranking.</param>
/// <param name="Priority">That site's rating. Higher is better.</param>
/// <param name="InfoHash">What copies of one release are merged by, when the site gives it.</param>
/// <param name="Magnet">The magnet, when the site publishes one.</param>
/// <param name="DetailUrl">The row's own page, which is the usual route to a torrent.</param>
/// <param name="Seeders">
/// How many are serving it, or null when the site does not say. Null is not
/// nought: judging a copy on a number nobody gave is the same category error as
/// judging a name on one.
/// </param>
/// <param name="SizeBytes">How big it is, or null when the site does not say.</param>
public sealed record ReleaseCopy(
    string Title,
    string Source,
    int Priority,
    string? InfoHash = null,
    string? Magnet = null,
    Uri? DetailUrl = null,
    int? Seeders = null,
    long? SizeBytes = null);
