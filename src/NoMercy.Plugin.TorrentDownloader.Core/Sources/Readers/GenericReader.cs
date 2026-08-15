using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// The reader for a site that publishes a route to the torrent on the listing
/// itself.
/// </summary>
/// <remarks>
/// It looks for the thing that matters — a magnet, or a <c>.torrent</c> address
/// with the info hash in it — and takes the row's own title from the anchor
/// beside it. LimeTorrents is the shipped source it is meant for: every row
/// carries a hashed <c>.torrent</c> link.
///
/// A source gets this reader by naming none. Nothing falls back into it by
/// accident: that is C4, where two missing per-site readers landed here and
/// answered enough rows that nobody noticed for a release.
/// </remarks>
public sealed class GenericReader : ISourceReader
{
    /// <summary>A row of a table, which is where every one of these sites puts a release.</summary>
    private static readonly Regex Rows = new(
        "<tr[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>A <c>.torrent</c> address carrying a forty-character hash.</summary>
    private static readonly Regex HashedTorrent = new(
        @"href=""([^""]*?([a-fA-F0-9]{40})[^""]*?\.torrent[^""]*)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Any anchor, so the row's own page and title can be picked out of one.</summary>
    private static readonly Regex Anchors = new(
        @"<a\s[^>]*href=""([^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>
    /// The kind it is the reader for, because that is how the catalogue asks
    /// for it: a source of kind <c>site</c> naming no reader of its own means
    /// this one, and there is no second way of saying it.
    /// </summary>
    public string Name => "site";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;

            string? magnet = Html.Magnet(markup);
            Match hashed = HashedTorrent.Match(markup);

            if (magnet is null && !hashed.Success)
            {
                // No route to a torrent is not a release row: it is a header, a
                // navigation strip or an advert.
                continue;
            }

            // The longest anchor text in the row. A release name is the longest
            // thing anybody writes in one, and picking the first anchor gets the
            // download icon's empty one instead.
            string title = Anchors.Matches(markup)
                .Select(anchor => Html.Text(anchor.Groups[2].Value))
                .OrderByDescending(text => text.Length)
                .FirstOrDefault(string.Empty);

            if (title.Length == 0)
            {
                continue;
            }

            // The row's own page, which is the anchor that is not the download.
            Uri? detail = Anchors.Matches(markup)
                .Where(anchor => Html.Text(anchor.Groups[2].Value) == title)
                .Select(anchor => Html.Absolute(anchor.Groups[1].Value, from))
                .FirstOrDefault();

            rows.Add(new(
                title,
                detail,
                magnet,
                hashed.Success ? hashed.Groups[2].Value.ToUpperInvariant() : Html.OnlyHash(magnet ?? string.Empty),
                Html.Count(Cell(markup, "tdseed")),
                Html.Count(Cell(markup, "tdleech")),
                Html.Size(markup)));
        }

        return rows;
    }

    /// <summary>The contents of a cell with this class, if the row has one.</summary>
    private static string? Cell(string markup, string className)
    {
        Match found = new Regex(
                $@"<td[^>]*class=""[^""]*{Regex.Escape(className)}[^""]*""[^>]*>(.*?)</td>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Match(markup);

        return found.Success ? found.Groups[1].Value : null;
    }
}
