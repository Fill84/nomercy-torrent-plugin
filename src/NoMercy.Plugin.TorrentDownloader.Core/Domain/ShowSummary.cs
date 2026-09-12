namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// One show as the Shows page needs it: what is outstanding, counted.
/// </summary>
/// <remarks>
/// Every number here is counted from the rows that exist, never carried from
/// the library's own totals. A count that can disagree with the list beneath it
/// is the shape of 0.3.4 showing "0 downloads" while two were running.
/// </remarks>
/// <param name="ShowId">The provider's show id.</param>
/// <param name="Title">The library's title.</param>
/// <param name="Year">Its first air date's year, or null.</param>
/// <param name="Kind">Television or anime, as the server filed it.</param>
/// <param name="Missing">Aired, no file, still being looked for — however many times.</param>
/// <param name="WaitingToAir">Not aired yet. Never counted as missing.</param>
public sealed record ShowSummary(
    int ShowId,
    string Title,
    int? Year,
    LibraryKind Kind,
    int Missing,
    int WaitingToAir);
