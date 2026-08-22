using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// TorrentGalaxy: rows that are not table rows, and a page full of hashes that
/// are not hashes.
/// </summary>
/// <remarks>
/// <strong>E6.</strong> The forty-character hex strings on this page are
/// element ids — seven distinct ones in the capture, and not a magnet anywhere.
/// Taking the first would attach a stranger's hash to a release, so nothing
/// here reads one at all.
///
/// The title comes off the anchor's <c>title</c> attribute. The text is split
/// across spans, and joining the nodes glues words together.
/// </remarks>
public sealed class TorrentGalaxyReader : ISourceReader
{
    private static readonly Regex Rows = new(
        @"<div class=""tgxtablerow[^""]*""(.*?)(?=<div class=""tgxtablerow|</table>)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Release = new(
        @"<a\s[^>]*title=""([^""]+)""\s+href=""(/post-detail/[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Seeders and leechers, two tags away from the bracket.</summary>
    private static readonly Regex Health = new(
        @"title=""Seeders/Leechers""[^>]*>\s*\[.*?<b>\s*([0-9,]+)\s*</b>.*?<b>\s*([0-9,]+)\s*</b>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex SizeBadge = new(
        @"<span class=""badge badge-secondary""[^>]*>([^<]+)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "torrentgalaxy";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match release = Release.Match(markup);

            if (!release.Success)
            {
                continue;
            }

            Match health = Health.Match(markup);
            Match size = SizeBadge.Match(markup);

            rows.Add(new(
                Html.Decode(release.Groups[1].Value),
                Html.Absolute(release.Groups[2].Value, from),
                // No magnet and no hash: see the remarks. The row's own page is
                // the route.
                Seeders: health.Success ? Html.Count(health.Groups[1].Value) : null,
                Leechers: health.Success ? Html.Count(health.Groups[2].Value) : null,
                SizeBytes: size.Success ? Html.Size(size.Groups[1].Value) : null));
        }

        return rows;
    }
}

/// <summary>
/// Torrentz2: a definition list per release, with somebody else's site name in
/// front of half the titles.
/// </summary>
/// <remarks>
/// The prefix is cut on <c> - </c> with spaces around it, and nothing else: a
/// scene name is full of dashes and the one before the release group has none.
/// Cutting on a bare dash would take the group off every title.
/// </remarks>
public sealed class Torrentz2Reader : ISourceReader
{
    private static readonly Regex Entries = new(
        "<dl>(.*?)</dl>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Release = new(
        @"<a\s[^>]*href=""(/torrent/[^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Span = new(
        @"<span class=""([sud])""[^>]*>(.*?)</span>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>A site's name in front of the release, ending in a spaced dash.</summary>
    private static readonly Regex ForeignPrefix = new(
        @"^\s*(?:www\.)?[A-Za-z0-9.-]+\.[A-Za-z]{2,}\s+-\s+",
        RegexOptions.Compiled);

    public string Name => "torrentz2";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match entry in Entries.Matches(body))
        {
            string markup = entry.Groups[1].Value;
            Match release = Release.Match(markup);

            if (!release.Success)
            {
                continue;
            }

            Dictionary<string, string> spans = Span.Matches(markup)
                .GroupBy(span => span.Groups[1].Value)
                .ToDictionary(span => span.Key, span => span.First().Groups[2].Value);

            rows.Add(new(
                ForeignPrefix.Replace(Html.Text(release.Groups[2].Value), string.Empty),
                Html.Absolute(release.Groups[1].Value, from),
                Seeders: Html.Count(spans.GetValueOrDefault("u")),
                Leechers: Html.Count(spans.GetValueOrDefault("d")),
                SizeBytes: Html.Size(spans.GetValueOrDefault("s"))));
        }

        return rows;
    }
}

/// <summary>
/// TorrentDownloads: real rows among adverts carrying the same words.
/// </summary>
/// <remarks>
/// The first links on the page are advertisements for another site, and they
/// name the search term, so they look exactly like results. Every real release
/// has a numeric id in its address and no advert does — that is what separates
/// them, and matching on anything else takes the adverts too.
/// </remarks>
public sealed class TorrentDownloadsReader : ISourceReader
{
    private static readonly Regex Rows = new(
        @"<div class=""grey_bar3[^""]*"">(.*?)</div>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The numeric id every real release has, and no advert does.</summary>
    private static readonly Regex Release = new(
        @"<a\s[^>]*href=""(/torrent/\d+/[^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>Two bare spans in that order, then the size.</summary>
    private static readonly Regex Counts = new(
        @"</span><span>([0-9,]+)</span><span>([0-9,]+)</span><span>([^<]+)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "torrentdownloads";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match release = Release.Match(markup);

            if (!release.Success)
            {
                continue;
            }

            Match counts = Counts.Match(markup);

            if (Named(Html.Text(release.Groups[2].Value)) is not string title)
            {
                continue;
            }

            rows.Add(new(
                title,
                Html.Absolute(release.Groups[1].Value, from),
                Seeders: counts.Success ? Html.Count(counts.Groups[1].Value) : null,
                Leechers: counts.Success ? Html.Count(counts.Groups[2].Value) : null,
                SizeBytes: counts.Success ? Html.Size(counts.Groups[3].Value) : null));
        }

        return rows;
    }

    /// <summary>
    /// The release's name with the file type this site writes after it taken
    /// off, or nothing at all when that type is not a video.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Some rows here end in the type of the file inside them:
    /// <c>Silo S03E06 MULTI 1080p WEB H264-HiggsBoson exe</c>,
    /// <c>silo s03e06 1080p web h264-cakes[EZTVx to] mkv</c>. It is not part of
    /// the release name, and leaving it on writes a name against the grab that
    /// staging then matches a finished file by and never finds.
    /// </para>
    /// <para>
    /// What may pass is <c>Staging.VideoExtensions</c>, the same whitelist that
    /// decides which files are downloaded. This row is refused for any other
    /// type rather than for a list of bad ones: on 22 August 2026 the list of
    /// bad ones lived only here, and a 1.2 GB executable from a different site
    /// went straight past it.
    /// </para>
    /// </remarks>
    private static string? Named(string printed)
    {
        if (TitleMatcher.FileType(printed) is not string type)
        {
            // The release group, which most rows end in. Left exactly as the
            // site printed it.
            return printed;
        }

        return Staging.VideoExtensions.Contains("." + type)
            ? printed[..(printed.Length - type.Length - 1)].TrimEnd()
            : null;
    }
}

/// <summary>
/// TorrentBay: rows whose magnet is not on the page at all.
/// </summary>
/// <remarks>
/// <strong>D4.</strong> <c>[GeneratedRegex]</c> was measured returning zero
/// matches here where the identical inline expression returned fifty, so every
/// expression in this file is a <c>static readonly Regex</c> and this reader is
/// tested against the real capture with a non-zero row count.
///
/// The magnet is fetched by the site's own script from an endpoint this page
/// does not name; each row carries the id it would be asked for. Getting one is
/// a signed request from inside the browser session and is not this reader's
/// work — the reader's job is to produce the row and the id it needs.
/// </remarks>
public sealed class TorrentBayReader : ISourceReader
{
    private static readonly Regex Rows = new(
        "<tr[^>]*>(.*?)</tr>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    private static readonly Regex Release = new(
        @"<a\s[^>]*href=""([^""]+)""[^>]*class=""torrent-title-link""[^>]*>(.*?)</a>",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);

    /// <summary>The id the magnet is asked for by.</summary>
    /// <remarks>
    /// The class comes after the address on this page and before the id, so the
    /// two are read in the order the page writes them rather than the order the
    /// request wants them.
    /// </remarks>
    private static readonly Regex MagnetId = new(
        @"class=""[^""]*search-magnet-btn[^""]*""[^>]*data-id=""(\d+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The token the page declares for its own script to sign with.</summary>
    private static readonly Regex PageToken = new(
        @"window\.searchPageToken\s*=\s*['""]([^'""]+)['""]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The session the page was served to, which the request carries back.</summary>
    private static readonly Regex SessionId = new(
        @"<meta\s+name=""csrf-token""\s+content=""([^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Leechers = new(
        @"<span class=""text-danger"">([0-9,]+)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Seeders = new(
        @"<span class=""text-success"">([0-9,]+)</span>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Name => "torrentbay";

    public IReadOnlyList<SourceRow> Read(string body, Uri from)
    {
        List<SourceRow> rows = [];

        // Read once for the page rather than once per row: both belong to the
        // page, and a row carrying a token from a different one is refused.
        Match token = PageToken.Match(body);
        Match session = SessionId.Match(body);

        foreach (Match row in Rows.Matches(body))
        {
            string markup = row.Groups[1].Value;
            Match release = Release.Match(markup);

            if (!release.Success)
            {
                continue;
            }

            Match seeders = Seeders.Match(markup);
            Match leechers = Leechers.Match(markup);

            rows.Add(new(
                // Read whole and stripped: the name is cut into spans, and
                // joining the nodes runs the words together.
                Html.Text(release.Groups[2].Value),
                Html.Absolute(release.Groups[1].Value, from),
                Seeders: seeders.Success ? Html.Count(seeders.Groups[1].Value) : null,
                Leechers: leechers.Success ? Html.Count(leechers.Groups[1].Value) : null,
                SizeBytes: Html.Size(markup),

                // What this site has to be asked for the torrent, since it
                // prints neither a magnet nor a hash anywhere. A row missing
                // any of the three cannot be asked at all, and says so by
                // carrying no claim rather than by carrying half of one.
                Claim: ClaimOn(markup, token, session)));
        }

        return rows;
    }

    /// <summary>The row's id and the page's two tokens, when the page has all three.</summary>
    private static SignedClaim? ClaimOn(string markup, Match token, Match session)
    {
        Match id = MagnetId.Match(markup);

        return id.Success && token.Success && session.Success
            ? new(id.Groups[1].Value, token.Groups[1].Value, session.Groups[1].Value)
            : null;
    }
}
