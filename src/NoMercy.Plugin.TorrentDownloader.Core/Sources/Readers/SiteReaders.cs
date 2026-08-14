using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// 1337x: a table of rows, each naming its own page.
/// </summary>
/// <remarks>
/// No magnet on the listing at all — the row carries its own page address and
/// the magnet is on it. 0.3.4 wrote that address and read it nowhere, which is
/// how a source produced rows for weeks and no downloads.
/// </remarks>
public sealed class X1337Reader : ISourceReader
{
    private static readonly Regex NameCells = new(
        @"<td[^>]*class=""[^""]*coll-1[^""]*""[^>]*>(.*?)</td>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The release anchor, which is the one pointing at a torrent's page.</summary>
    private static readonly Regex Release = new(
        @"<a\s[^>]*href=""(/torrent/[^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Rows = new(
        "<tr[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public string Name => "1337x";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match name = NameCells.Match(markup);

            if (!name.Success)
            {
                continue;
            }

            Match release = Release.Match(name.Groups[1].Value);

            if (!release.Success)
            {
                continue;
            }

            rows.Add(new(
                Html.Text(release.Groups[2].Value),
                Html.Absolute(release.Groups[1].Value, from),
                Seeders: Html.Count(Cells.Of(markup, "coll-2")),
                Leechers: Html.Count(Cells.Of(markup, "coll-3")),
                // The size cell has the seed count nested inside it, so it is
                // read as text rather than as a number.
                SizeBytes: Html.Size(Cells.Of(markup, "coll-4"))));
        }

        return rows;
    }
}

/// <summary>
/// EZTV: one anchor per episode, carrying the whole name.
/// </summary>
/// <remarks>
/// Every title ends in the site's own tag and it has to go, or nothing matches
/// a release name. The document says <c>[eztv.re]</c>; the page as captured on
/// 14 August 2026 says <c>[eztv]</c>. Both are stripped, because a site that
/// has changed this once will change it again.
///
/// The listing carries no magnet either — the links are behind a form — so the
/// row's own page is the route, as with 1337x.
/// </remarks>
public sealed class EztvReader : ISourceReader
{
    private static readonly Regex Episodes = new(
        @"<a\s[^>]*href=""([^""]+)""[^>]*class=""epinfo""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The site's own tag, in either of the forms it has used.</summary>
    private static readonly Regex Suffix = new(
        @"\s*\[eztv(?:\.re)?\]\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Rows = new(
        "<tr[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public string Name => "eztv";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match episode = Episodes.Match(markup);

            if (!episode.Success)
            {
                continue;
            }

            rows.Add(new(
                Suffix.Replace(Html.Text(episode.Groups[2].Value), string.Empty),
                Html.Absolute(episode.Groups[1].Value, from),
                Html.Magnet(markup),
                Seeders: null,
                SizeBytes: Html.Size(markup)));
        }

        return rows;
    }
}

/// <summary>
/// KickassTorrents: a name cut into highlighted fragments, and sometimes no
/// listing at all.
/// </summary>
/// <remarks>
/// <strong>E4.</strong> A search for a full release name often answers with
/// that release's own page rather than a listing. A reader that finds no rows
/// there and gives up throws away the one result the site actually had, so when
/// there are no rows a magnet anywhere on the page is taken as the answer.
///
/// The name lives in <c>cellMainLink</c> and is cut into fragments by a span
/// colouring the matched words, so the anchor is read whole and stripped rather
/// than assembled from its text nodes.
/// </remarks>
public sealed class KickassReader : ISourceReader
{
    private static readonly Regex MainLinks = new(
        @"<a\s[^>]*href=""([^""]+)""[^>]*class=""cellMainLink""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Rows = new(
        "<tr[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Title = new(
        "<title[^>]*>(.*?)</title>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    public string Name => "kickass";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match main = MainLinks.Match(markup);

            if (!main.Success)
            {
                continue;
            }

            rows.Add(new(
                Html.Text(main.Groups[2].Value),
                Html.Absolute(main.Groups[1].Value, from),
                Html.Magnet(markup),
                Seeders: Html.Count(Cells.Of(markup, "green")),
                Leechers: Html.Count(Cells.Of(markup, "red lasttd")),
                SizeBytes: Html.Size(Cells.Of(markup, "nobr"))));
        }

        if (rows.Count > 0)
        {
            return rows;
        }

        // No listing. This is the release's own page, reached by a redirect the
        // search caused, and the magnet on it is the answer the search had.
        string? magnet = Html.Magnet(body);

        if (magnet is null)
        {
            return rows;
        }

        Match title = Title.Match(body);

        return
        [
            new(
                title.Success ? Html.Text(title.Groups[1].Value) : Html.Text(magnet),
                from,
                magnet,
                Html.OnlyHash(magnet)),
        ];
    }
}

/// <summary>Reading a table cell by the class on it.</summary>
internal static class Cells
{
    public static string? Of(string markup, string className)
    {
        Match found = new Regex(
                $@"<td[^>]*class=""[^""]*{Regex.Escape(className)}[^""]*""[^>]*>(.*?)</td>",
                RegexOptions.Singleline | RegexOptions.IgnoreCase)
            .Match(markup);

        return found.Success ? found.Groups[1].Value : null;
    }
}
