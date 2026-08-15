using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

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
public sealed record SourceRow(
    string Title,
    Uri? DetailUrl = null,
    string? Magnet = null,
    string? InfoHash = null,
    int? Seeders = null,
    int? Leechers = null,
    long? SizeBytes = null);

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
    private static readonly Regex Tags = new("<[^>]*>", RegexOptions.Compiled | RegexOptions.Singleline);
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
    /// the nodes glues them together — TorrentGalaxy and TorrentFunk both do
    /// this, and both lost the release group to it.
    /// </remarks>
    public static string Text(string markup)
    {
        return Decode(Spaces.Replace(Tags.Replace(markup, " "), " ").Trim());
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
