// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// A torrent site the owner named, searched as an indexer.
///
/// <para>
/// This is the half that makes a feed useful. A feed announces that a release exists and
/// what it is called; nothing in it can be downloaded. A site is where the magnet lives,
/// and the exact name from the feed is what you search it with - which is also what makes
/// the result trustworthy, because a row whose title is that release is the release rather
/// than something hopefully similar.
/// </para>
///
/// <para>
/// The owner supplies the search URL with <c>{query}</c> where the terms go, because that
/// is the one thing that genuinely differs between sites and the one thing they can read
/// off their own address bar. Everything after the fetch is generic - see
/// <see cref="SiteListingParser"/> for why that works.
/// </para>
/// </summary>
public sealed class SiteIndexer(
    string name,
    int priority,
    string searchUrlTemplate,
    ChallengeAwareFetch fetch
) : IIndexer
{
    /// <summary>What the owner puts in the URL where the search terms belong.</summary>
    public const string QueryPlaceholder = "{query}";

    /// <summary>
    /// The other spellings of the same idea, accepted and rewritten to
    /// <see cref="QueryPlaceholder"/> on the way in.
    ///
    /// <para>
    /// Nobody reads a label and then types the exact token it named. They write the thing
    /// that means "put it here" - <c>&lt;replace&gt;</c>, <c>%s</c>, <c>{search}</c> - and a
    /// plugin that accepts only one of them is refusing an address that is right in every
    /// way that matters. The site's URL is the hard part; the token is not.
    /// </para>
    ///
    /// <para>
    /// Longest first, so <c>&lt;searchterm&gt;</c> is not half-consumed by
    /// <c>&lt;search&gt;</c>.
    /// </para>
    /// </summary>
    public static readonly string[] AcceptedPlaceholders =
    [
        QueryPlaceholder,
        "{searchterms}",
        "{searchterm}",
        "<searchterms>",
        "<searchterm>",
        "{replace}",
        "<replace>",
        "{search}",
        "<search>",
        "<query>",
        "{term}",
        "<term>",
        "{q}",
        "<q>",
        "{0}",
        "%s",
    ];

    /// <summary>
    /// The same address written the way the rest of this plugin expects it.
    ///
    /// <para>
    /// Applied when settings are saved rather than per search, so what is stored is what is
    /// used and the config file does not carry a spelling only this method understands.
    /// </para>
    /// </summary>
    public static string Normalise(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return template ?? string.Empty;

        string normalised = template;

        foreach (string alias in AcceptedPlaceholders)
        {
            if (alias == QueryPlaceholder)
                continue;

            normalised = normalised.Replace(alias, QueryPlaceholder, StringComparison.OrdinalIgnoreCase);
        }

        return normalised;
    }

    public string Name => name;

    public int Priority => priority;

    /// <summary>Whether a template can be used at all. Checked when settings are saved, not per search.</summary>
    public static bool IsUsableTemplate(string? template) =>
        !string.IsNullOrWhiteSpace(template)
        && template.Contains(QueryPlaceholder, StringComparison.Ordinal)
        && Uri.TryCreate(template.Replace(QueryPlaceholder, "x", StringComparison.Ordinal), UriKind.Absolute, out _);

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        string terms = Terms(query);

        // A site cannot be asked for everything. Torznab and RSS both answer an empty
        // query with something useful; a search page answers it with its front page, or
        // with every torrent it has. Neither is what the feed cadence wants.
        if (terms.Length == 0)
            return [];

        Uri url = new(searchUrlTemplate.Replace(QueryPlaceholder, Uri.EscapeDataString(terms), StringComparison.Ordinal));

        string html = await fetch.GetStringAsync(url, name, "search", ct);

        return
        [
            .. SiteListingParser.Parse(html).Select(row => new ReleaseInfo
            {
                IndexerName = name,
                TorrentId = row.InfoHash ?? row.Title,
                Title = row.Title,
                InfoHash = row.InfoHash,
                MagnetUri = row.MagnetUri,
                Seeders = row.Seeders,
                IndexerPriority = priority,
            }),
        ];
    }

    /// <summary>
    /// The search terms, as a person would type them.
    ///
    /// <para>
    /// Show and slot together, because a site search matches on the whole string and
    /// "South Park" alone returns a decade of it. Dots and dashes are left out: sites
    /// tokenise the query themselves, and a scene-formatted string matches fewer rows than
    /// the words it is made of.
    /// </para>
    /// </summary>
    private static string Terms(SearchQuery query)
    {
        string show = (query.ShowName ?? string.Empty).Trim();

        return query.Slot is { } slot
            ? $"{show} S{slot.Season:D2}E{slot.Episode:D2}".Trim()
            : show;
    }
}
