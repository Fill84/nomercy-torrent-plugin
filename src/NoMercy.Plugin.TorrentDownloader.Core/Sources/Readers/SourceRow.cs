using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

/// <summary>
/// What a site has to be asked, in its own words, before it will name a row's
/// torrent.
/// </summary>
/// <remarks>
/// <para>
/// TorrentBay publishes no magnet and no hash, on the listing or on the row's
/// own page: both carry a button and an id, and the magnet comes back from a
/// signed request to the site's own endpoint. Everything that request needs is
/// on the page it was read from — the id off the row, and two tokens off the
/// page — so it is carried with the row rather than fetched again later, and a
/// token from one page is never sent with a row from another.
/// </para>
/// <para>
/// The signature is over the id, the moment, and the page token. That is the
/// site's own rule, taken from the script the page loads, and it is why this
/// cannot be a plain address: the moment is part of what is signed, so the
/// request has to be built when it is made.
/// </para>
/// </remarks>
/// <param name="TorrentId">The row's own id, off the button that would be pressed.</param>
/// <param name="PageToken">The token the page declared, which the signature is over.</param>
/// <param name="SessionId">The session the page was served to.</param>
public sealed record SignedClaim(string TorrentId, string PageToken, string SessionId);

/// <summary>
/// One row a source answered with.
/// </summary>
/// <remarks>
/// A row is a <em>candidate</em>, not a decision. Nothing here is judged
/// against the profile; that happens later and against the copy, not the page.
/// </remarks>
/// <param name="Title">The release name as the site printed it, tidied of markup and nothing else.</param>
/// <param name="DetailUrl">
/// The row's own page. Where a listing carries no magnet — which is most of
/// them — this is the only route to one, and 0.3.4 wrote this field and read it
/// nowhere: TorrentBay produced rows for weeks and zero downloads.
/// </param>
/// <param name="Magnet">The magnet, when the listing carries one.</param>
/// <param name="InfoHash">
/// The hash, when the page gives one. It is what copies of one release are
/// merged by, so a row with a hash and no magnet is still worth having.
/// </param>
/// <param name="Seeders">How many are serving it, or null when the page does not say.</param>
/// <param name="Leechers">How many are taking it, or null.</param>
/// <param name="SizeBytes">Its size, or null.</param>
/// <param name="Claim">
/// What this site must be asked before it will name the torrent, for a site
/// that publishes neither a magnet nor a hash anywhere.
/// </param>
public sealed record SourceRow(
    string Title,
    Uri? DetailUrl = null,
    string? Magnet = null,
    string? InfoHash = null,
    int? Seeders = null,
    int? Leechers = null,
    long? SizeBytes = null,
    SignedClaim? Claim = null);

/// <summary>
/// The small amount of HTML handling every reader needs.
/// </summary>
/// <remarks>
/// Regular expressions, because Core references nothing and a parser is not
/// worth a dependency this whole project is arranged to avoid. They are
/// <c>static readonly Regex</c> throughout: <c>[GeneratedRegex]</c> was
/// measured returning zero matches on TorrentBay where the identical inline
/// expression returned fifty, and zero rows is exactly what a site with nothing
/// looks like.
/// </remarks>
public static class Html
{
    /// <summary>A run of tags, however many are stacked together.</summary>
    /// <remarks>
    /// A run rather than one tag, because what matters is the gap the whole run
    /// leaves behind: <c>&lt;/span&gt;&lt;span&gt;</c> is one gap and two tags,
    /// and deciding it twice gets it wrong the second time.
    /// </remarks>
    private static readonly Regex Tags = new("(?:<[^>]*>)+", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex Hash = new("[a-fA-F0-9]{40}", RegexOptions.Compiled);
    private static readonly Regex Magnets = new(@"magnet:\?[^""'<\s]+", RegexOptions.Compiled);

    /// <summary>
    /// The words inside a fragment of markup, with the tags gone and runs of
    /// space collapsed.
    /// </summary>
    /// <remarks>
    /// Read whole and stripped, never joined from the text nodes. A name split
    /// by a span colouring the matched word is one word per node, and joining
    /// the nodes glues them together — 1337x does this, and lost the release
    /// group to it.
    /// </remarks>
    public static string Text(string markup)
    {
        return Decode(Spaces.Replace(Tags.Replace(markup, Gap), " ").Trim());
    }

    /// <summary>
    /// What a run of tags leaves behind: a space only where it was holding two
    /// words apart.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves of this are a real capture. 1337x writes a row whose words
    /// are held apart by nothing but a tag, and taking the tags out with nothing
    /// in their place glues them together — which is how a release group was
    /// lost in 0.3.4.
    /// </para>
    /// <para>
    /// TorrentBay writes
    /// <c>&lt;span&gt;Silo&lt;/span&gt;.&lt;span&gt;S03E08&lt;/span&gt;.1080p</c>
    /// — a scene name whose separators are already there — and putting a space
    /// in each gap made <c>Silo.S03E08.1080p.WEB.H264-CAKES</c> come off the
    /// page as <c>Silo . S03E08 .1080p.WEB.H264-CAKES</c>. Twenty-six of the
    /// thirty-four rows on the capture of 22 August 2026 were mangled that way,
    /// including the copy of the episode the owner's library was missing.
    /// </para>
    /// <para>
    /// So: a space when the run really is all that separates two words, and
    /// nothing when the page put a separator there itself.
    /// </para>
    /// </remarks>
    private static string Gap(Match run)
    {
        string markup = run.Result("$_");

        char before = run.Index > 0 ? markup[run.Index - 1] : ' ';
        int after = run.Index + run.Length;

        return char.IsLetterOrDigit(before) && after < markup.Length && char.IsLetterOrDigit(markup[after])
            ? " "
            : string.Empty;
    }

    /// <summary>
    /// A number written as an entity: <c>&amp;#45;</c> for a dash.
    /// </summary>
    /// <remarks>
    /// srrDB writes every dash in a release name this way, so
    /// <c>Persiana_Jones&amp;#45;Una_Vita</c> is a name that matches nothing at
    /// all until it is decoded — and a scene name is mostly dashes.
    /// </remarks>
    private static readonly Regex Numeric = new(
        "&#(x?)([0-9a-fA-F]+);",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The handful of entities a release name really contains.</summary>
    public static string Decode(string text)
    {
        return Numeric.Replace(text, match =>
            {
                bool hexadecimal = match.Groups[1].Value.Length > 0;
                int code = Convert.ToInt32(match.Groups[2].Value, hexadecimal ? 16 : 10);

                // A code point outside what a character can be is not one, and
                // leaving the entity as written says more than a question mark.
                return code is > 0 and <= 0x10FFFF ? char.ConvertFromUtf32(code) : match.Value;
            })
            .Replace("&amp;", "&", StringComparison.Ordinal)
            .Replace("&#039;", "'", StringComparison.Ordinal)
            .Replace("&apos;", "'", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Replace("&lt;", "<", StringComparison.Ordinal)
            .Replace("&gt;", ">", StringComparison.Ordinal)
            .Replace("&nbsp;", " ", StringComparison.Ordinal);
    }

    /// <summary>An absolute address for <paramref name="href"/>, or null when it is not one.</summary>
    public static Uri? Absolute(string? href, Uri from)
    {
        return string.IsNullOrWhiteSpace(href) || !Uri.TryCreate(from, Decode(href), out Uri? absolute)
            ? null
            : absolute;
    }

    /// <summary>The first magnet in <paramref name="markup"/>, if any.</summary>
    public static string? Magnet(string markup)
    {
        Match found = Magnets.Match(markup);

        return found.Success ? Decode(found.Value) : null;
    }

    /// <summary>
    /// A forty-character hash from <paramref name="markup"/>, when there is
    /// exactly one to be sure about.
    /// </summary>
    /// <remarks>
    /// <strong>E6.</strong> A dozen forty-hex strings on a TorrentGalaxy page
    /// are element ids, not info hashes. Taking the first would attach a
    /// stranger's hash to a release; taking one only when the page has a single
    /// one is the difference between a hash and a coincidence.
    /// </remarks>
    public static string? OnlyHash(string markup)
    {
        string[] hashes = [.. Hash.Matches(markup).Select(match => match.Value.ToUpperInvariant()).Distinct()];

        return hashes.Length == 1 ? hashes[0] : null;
    }

    /// <summary>A count like <c>4,417</c>, or null when the page does not say.</summary>
    public static int? Count(string? markup)
    {
        if (markup is null)
        {
            return null;
        }

        string text = Text(markup).Replace(",", string.Empty, StringComparison.Ordinal);

        return int.TryParse(text, out int count) ? count : null;
    }

    /// <summary>A size like <c>9.6 GB</c> in bytes, or null.</summary>
    /// <remarks>
    /// Powers of 1024, because that is what these sites mean by GB whatever the
    /// letter says — a 9.6 GB release measured 10,307,921,510 bytes.
    /// </remarks>
    public static long? Size(string? markup)
    {
        if (markup is null)
        {
            return null;
        }

        Match found = SizeText.Match(Text(markup));

        if (!found.Success || !double.TryParse(found.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double amount))
        {
            return null;
        }

        long unit = found.Groups[2].Value.ToUpperInvariant() switch
        {
            "KB" => 1024L,
            "MB" => 1024L * 1024,
            "GB" => 1024L * 1024 * 1024,
            "TB" => 1024L * 1024 * 1024 * 1024,
            _ => 1L,
        };

        return (long)(amount * unit);
    }

    private static readonly Regex SizeText = new(
        @"([0-9]+(?:\.[0-9]+)?)\s*(TB|GB|MB|KB|B)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
}
