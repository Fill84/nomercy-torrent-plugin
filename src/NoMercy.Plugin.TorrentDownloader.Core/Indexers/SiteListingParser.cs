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
/// What makes that possible is that torrent listings agree on one thing even when they
/// agree on nothing else: the magnet link is in the page, and it carries the release name
/// in its own <c>dn</c> parameter. That is the row's identity and its payload in a single
/// string, needing no knowledge of the surrounding markup at all.
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

        return [.. byMagnet.Values];
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

        Match match = LabelledSeedersPattern().Match(row);

        if (!match.Success)
            match = TrailingSeedersPattern().Match(row);

        return match.Success && int.TryParse(match.Groups[1].Value, out int seeders) ? seeders : 0;
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
