using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// Writing a search term into an address the way that site wants it.
/// </summary>
/// <remarks>
/// <strong>E3.</strong> A plus in a path is a plus. TorrentGalaxy answered
/// nothing for a release it has sixteen copies of, because the term arrived
/// with plus signs in it where the site expected spaces — and 1337x searches
/// from its path too and wants the opposite. Neither can be derived from the
/// address, so each site declares which it is.
/// </remarks>
public static class Query
{
    /// <summary>
    /// <paramref name="address"/> with <c>{query}</c> replaced by
    /// <paramref name="term"/>, written in <paramref name="style"/>.
    /// </summary>
    public static string Write(string address, string term, string style)
    {
        return address.Replace("{query}", Format(term, style), StringComparison.Ordinal);
    }

    /// <summary>The term as this style sends it.</summary>
    public static string Format(string term, string style)
    {
        return style switch
        {
            // As given, for an endpoint matching a string rather than tokenising.
            QueryStyles.Verbatim => Uri.EscapeDataString(term),

            // The site's search *is* the path segment, so anything that is not
            // a letter or a digit becomes one dash and runs collapse.
            QueryStyles.Slug => Slug(term),

            // Searching from a path, where a plus is a plus and not a space.
            QueryStyles.Spaced => string.Join("%20", Words(term)),

            // The default: punctuation to spaces, joined with a plus.
            _ => string.Join('+', Words(term)),
        };
    }

    /// <summary>
    /// The term as words, with punctuation gone.
    /// </summary>
    /// <remarks>
    /// A release name is full of dots and dashes that mean nothing to a search
    /// box: <c>Silo.S03E06.1080p.WEB.H264-CAKES</c> is six words, and a site
    /// asked for it verbatim answers nothing.
    /// </remarks>
    private static IEnumerable<string> Words(string term)
    {
        return term
            .Select(character => char.IsAsciiLetterOrDigit(character) ? character : ' ')
            .Aggregate(new StringBuilder(), (text, character) => text.Append(character))
            .ToString()
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string Slug(string term)
    {
        return string.Join('-', Words(term)).ToLowerInvariant();
    }
}
