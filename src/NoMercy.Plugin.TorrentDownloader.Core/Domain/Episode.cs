namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// One episode, as the library knows it.
/// </summary>
/// <param name="Key">Which show, season and number.</param>
/// <param name="Title">
/// The library's title for it, or null. Null stays null: an episode whose title
/// the server does not have is not an episode called nothing, and a page saying
/// which is more use than a blank.
/// </param>
/// <param name="AirDate">
/// When it aired, or null when no date is announced. A date, not a moment: the
/// library holds a broadcast day, and the hours attached to it in the database
/// are not a time anything aired at.
/// </param>
/// <param name="HasFile">
/// Whether the library holds a file for it. This is the only thing that decides
/// whether an episode is present — see <c>Show</c> for why the show's own count
/// is not on offer.
/// </param>
public sealed record Episode(
    EpisodeKey Key,
    string? Title,
    DateOnly? AirDate,
    bool HasFile)
{
    public int Season => Key.Season;

    public int Number => Key.Number;
}
