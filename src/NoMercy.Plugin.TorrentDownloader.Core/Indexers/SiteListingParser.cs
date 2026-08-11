// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.RegularExpressions;
using System.Web;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// Reading a torrent site's search results without knowing the site.
///
/// <para>
/// Written generically on purpose. The alternative is a parser per site, and a parser per
/// site is a plugin that breaks every time one of them redesigns - with the owner unable
/// to add the site they actually use until somebody ships a release. The owner names a
/// search URL; this reads whatever comes back.
/// </para>
///
/// <para>
/// Two shapes are understood, because assuming one of them was wrong. A magnet carries the
/// release name in its own <c>dn</c> parameter - identity and payload in a single string,
/// needing no knowledge of the surrounding markup. A great many sites have no magnet at
/// all, and link a torrent file whose name is the infohash: a magnet can be built from
/// that, and building it beats following the link, because the file usually lives on a
/// third host the owner never granted and should not have to.
/// </para>
///
/// <para>
/// The one-shape assumption cost a fortnight of silence on a real server. A configured site
/// answered every search with four usable releases and the parser found none of them,
/// because it looked only for magnets and that page has none.
/// </para>
///
/// <para>
/// Seeders are read when the page makes it obvious and left at zero when it does not. Zero
/// is honest - the profile's minimum-seeders rule then refuses the row, which is the right
/// outcome for a site whose listing cannot be trusted to say.
/// </para>
/// </summary>
public static partial class SiteListingParser
{
    /// <summary>
    /// A magnet anywhere in the document. Deliberately not anchored to an <c>href</c>:
    /// plenty of sites put the magnet in a data attribute, a button, or plain text.
    /// </summary>
    [GeneratedRegex(@"magnet:\?[^\s""'<>\\]+", RegexOptions.IgnoreCase)]
    private static partial Regex MagnetPattern();

    /// <summary>The display name a magnet carries. It is the release name, which is the whole reason this works.</summary>
    [GeneratedRegex(@"[?&]dn=([^&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex DisplayNamePattern();

    [GeneratedRegex(@"[?&]xt=urn:btih:([a-fA-F0-9]{40}|[a-zA-Z2-7]{32})", RegexOptions.IgnoreCase)]
    private static partial Regex InfoHashPattern();

    /// <summary>
    /// "Seeders: 42" - the label first, which is the form worth trusting because the
    /// number is unambiguously the one it names.
    ///
    /// <para>
    /// Only whitespace and a colon may separate them. Allowing any dozen characters lets
    /// it jump a table cell and read the next column instead: "148 seeders&lt;/td&gt;
    /// &lt;td&gt;3" reported three.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"seed(?:er)?s?\s{0,3}[:=]?\s{0,3}(\d{1,6})", RegexOptions.IgnoreCase)]
    private static partial Regex LabelledSeedersPattern();

    /// <summary>
    /// "42 seeders" - the other common form, and the reason it is tried second. A regex
    /// that allows either order matches leftmost, and leftmost in a table row is whatever
    /// number happened to sit before the label: the file size, the page number, the year.
    /// A listing reading "2.1 GB ... Seeders: 148" reported one seeder until this was
    /// split in two.
    /// </summary>
    [GeneratedRegex(@"(\d{1,6})\s{0,4}seed(?:er)?s?", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingSeedersPattern();

    /// <summary>
    /// A link to a torrent file whose name is the infohash, which is how a site with no
    /// magnet still says exactly which torrent a row is.
    ///
    /// <para>
    /// The hash is the file name rather than anything in the markup, so this needs to know
    /// nothing about the site - the same property that makes the magnet form work.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"href=[""']([^""']*?/([a-fA-F0-9]{40})\.torrent(?:\?[^""']*)?)[""']", RegexOptions.IgnoreCase)]
    private static partial Regex HashedTorrentPattern();

    /// <summary>
    /// The release name as the site printed it, taken from the first link text after the
    /// torrent link.
    ///
    /// <para>
    /// Preferred over the <c>title=</c> the torrent URL usually carries, because that one is
    /// slugged: "Silo-S03E04-1080p-HEVC-x265-MeGusta" turns the separator between show and
    /// quality into the same character used inside the group name, and the release parser
    /// then has to guess. The anchor beside it holds the name unmangled.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"<a\b[^>]*>\s*([^<>]{4,300}?)\s*</a>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTextPattern();

    /// <summary>The slugged name in the torrent URL, for a site that prints no link text worth reading.</summary>
    [GeneratedRegex(@"[?&]title=([^&""']+)", RegexOptions.IgnoreCase)]
    private static partial Regex UrlTitlePattern();

    /// <summary>
    /// A seeder count in a table cell named for it, thousands separator and all:
    /// <c>class="tdseed"&gt;3,038&lt;/td&gt;</c>.
    ///
    /// <para>
    /// The labelled and trailing forms below both miss this - they allow only whitespace and
    /// a colon between the word and the number, and here the markup itself sits between
    /// them. Read as zero, a minimum-seeders rule of two refuses every row on the site, so a
    /// parser that found the release and missed this would still download nothing.
    /// </para>
    /// </summary>
    [GeneratedRegex(@"seed[^""'>]*[""']?\s*>\s*([\d,. ]{1,12})\s*<", RegexOptions.IgnoreCase)]
    private static partial Regex CellSeedersPattern();

    /// <summary>
    /// How far either side of a magnet to look for its seeder count. A listing row is
    /// rarely longer than this, and reaching further starts reading the next row's number.
    /// </summary>
    private const int RowWindow = 600;

    public static IReadOnlyList<SiteRow> Parse(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return [];

        Dictionary<string, SiteRow> byMagnet = [];

        foreach (Match match in MagnetPattern().Matches(html))
        {
            string magnet = HttpUtility.HtmlDecode(match.Value);

            string? title = Name(magnet);

            // No name means nothing can match it to an episode, and a torrent nobody can
            // identify is worse than one nobody found - it would download and then have
            // no library to belong to.
            if (title is null)
                continue;

            // Keyed by magnet so a site that repeats a row - a listing and a details panel
            // for the same torrent - counts once.
            byMagnet.TryAdd(magnet, new SiteRow
            {
                Title = title,
                MagnetUri = magnet,
                InfoHash = Hash(magnet),
                Seeders = Seeders(html, match.Index),
            });
        }

        foreach (Match match in HashedTorrentPattern().Matches(html))
        {
            string hash = match.Groups[2].Value.ToLowerInvariant();
            string? title = TitleAfter(html, match.Index + match.Length)
                ?? Slug(HttpUtility.HtmlDecode(match.Groups[1].Value));

            if (title is null)
                continue;

            string magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(title)}";

            // A site that offers both forms for one torrent - the magnet on the row and the
            // file on the details link - must still count once, and the magnet found above
            // is the better of the two because it carries the site's own trackers.
            if (byMagnet.Values.Any(row => row.InfoHash == hash))
                continue;

            byMagnet.TryAdd(magnet, new SiteRow
            {
                Title = title,
                MagnetUri = magnet,
                InfoHash = hash,
                Seeders = Seeders(html, match.Index),
            });
        }

        return [.. byMagnet.Values];
    }

    /// <summary>
    /// The text of the first link following the torrent link, which on every listing of this
    /// shape is the release name the row is about.
    ///
    /// <para>
    /// Bounded to the row: reaching past the end of it picks up the next release's name and
    /// files this torrent under the wrong episode, which is worse than not finding a name at
    /// all.
    /// </para>
    /// </summary>
    private static string? TitleAfter(string html, int from)
    {
        if (from >= html.Length)
            return null;

        int length = Math.Min(html.Length - from, RowWindow);
        Match match = LinkTextPattern().Match(html.Substring(from, length));

        if (!match.Success)
            return null;

        string text = HttpUtility.HtmlDecode(match.Groups[1].Value).Trim();

        return text.Length == 0 ? null : text;
    }

    private static string? Slug(string url)
    {
        Match match = UrlTitlePattern().Match(url);

        if (!match.Success)
            return null;

        string name = Uri.UnescapeDataString(match.Groups[1].Value).Replace('+', ' ').Trim();

        return name.Length == 0 ? null : name;
    }

    private static string? Name(string magnet)
    {
        Match match = DisplayNamePattern().Match(magnet);

        if (!match.Success)
            return null;

        // Plus signs are spaces in a query string, and scene names arrive both ways.
        string name = Uri.UnescapeDataString(match.Groups[1].Value).Replace('+', ' ').Trim();

        return name.Length == 0 ? null : name;
    }

    private static string? Hash(string magnet)
    {
        Match match = InfoHashPattern().Match(magnet);

        return match.Success ? match.Groups[1].Value.ToLowerInvariant() : null;
    }

    private static int Seeders(string html, int magnetAt)
    {
        int start = Math.Max(0, magnetAt - RowWindow);
        int length = Math.Min(html.Length - start, RowWindow * 2);

        string row = html.Substring(start, length);

        // The cell form first: it is the only one of the three that names the number by the
        // markup around it rather than by nearby words, so when it matches it is right.
        Match match = CellSeedersPattern().Match(row);

        if (!match.Success)
            match = LabelledSeedersPattern().Match(row);

        if (!match.Success)
            match = TrailingSeedersPattern().Match(row);

        if (!match.Success)
            return 0;

        // Thousands separators, whichever the site's locale uses. A row reading 3,038 is
        // three thousand seeders and not three.
        string digits = new([.. match.Groups[1].Value.Where(char.IsAsciiDigit)]);

        return int.TryParse(digits, out int seeders) ? seeders : 0;
    }
}

/// <summary>One row read off a listing: what it is called, where to get it, and how well seeded.</summary>
public sealed record SiteRow
{
    public required string Title { get; init; }
    public required string MagnetUri { get; init; }
    public string? InfoHash { get; init; }
    public int Seeders { get; init; }
}
