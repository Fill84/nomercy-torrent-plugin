using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// Reading and writing the one address a torrent client can be handed.
/// </summary>
public static class Magnets
{
    private static readonly Regex HashInMagnet = new(
        @"xt=urn:btih:([a-zA-Z0-9]{32,40})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrackerInMagnet = new(
        @"[?&]tr=([^&\s""'<]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The info hash a magnet carries, upper case.</summary>
    public static string? HashOf(string? magnet)
    {
        if (magnet is null)
        {
            return null;
        }

        Match found = HashInMagnet.Match(magnet);

        return found.Success ? found.Groups[1].Value.ToUpperInvariant() : null;
    }

    /// <summary>Every tracker a magnet names, without duplicates and decoded.</summary>
    /// <remarks>
    /// They arrive percent-encoded inside the query, and a tracker address that
    /// is still encoded is one no client will announce to.
    /// </remarks>
    public static IReadOnlyList<string> TrackersOf(string? magnet)
    {
        if (magnet is null)
        {
            return [];
        }

        return
        [
            .. TrackerInMagnet.Matches(magnet)
                .Select(found => Uri.UnescapeDataString(found.Groups[1].Value))
                .Where(tracker => tracker.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>
    /// A magnet for a hash a page printed on its own.
    /// </summary>
    /// <remarks>
    /// TorrentFunk's detail page carries no magnet at all and prints the bare
    /// info hash, which is all a magnet needs. The trackers come from whatever
    /// else knows this torrent and from the owner's own list at the grab.
    /// </remarks>
    public static string For(string hash, string title)
    {
        return $"magnet:?xt=urn:btih:{hash.ToUpperInvariant()}&dn={Uri.EscapeDataString(title)}";
    }
}

/// <summary>
/// A row's own page, read for the one thing it is followed for.
/// </summary>
/// <remarks>
/// <strong>C3.</strong> No shipped indexer publishes a magnet on its listing —
/// not one of the nine captured — so this is the ordinary route to a torrent
/// rather than an exception. 0.3.4 wrote the address of the row's page and read
/// it nowhere: TorrentBay produced rows for weeks and zero downloads.
/// </remarks>
public static class DetailPage
{
    /// <summary>
    /// The magnet a detail page offers, built from its hash when it offers
    /// none.
    /// </summary>
    /// <returns>Null when the page names no torrent at all.</returns>
    public static (string Magnet, string Hash)? Read(string body, string title)
    {
        if (Html.Magnet(body) is string magnet && Magnets.HashOf(magnet) is string carried)
        {
            return (magnet, carried);
        }

        // Only when the page has exactly one, which is the difference between a
        // hash and a coincidence: a listing is full of forty-character element
        // ids, and taking the first would attach a stranger's hash to a
        // release.
        return Html.OnlyHash(body) is string hash ? (Magnets.For(hash, title), hash) : null;
    }
}
