using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;

/// <summary>
/// How many releases a page appears to carry, read without the reader.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of how a broken reader is told apart from a site that
/// honestly has nothing. Zero rows means one of two very different things, and
/// only the page can say which: if it is covered in releases and the reader saw
/// none of them, the reader is wrong about the site.
/// </para>
/// <para>
/// Counted as <em>names</em> and not as links, which is where
/// <c>docs/05-sources.md</c> was corrected. Six of the seventeen sources answer
/// JSON or XML with no anchor and no magnet anywhere in them — apibay carries a
/// name and a bare hash, srrDB a name and nothing else — so a count of links
/// would report every one of them as having nothing to offer on the day its
/// reader broke, which is precisely the fault this count exists to catch.
/// </para>
/// </remarks>
public static class PageReleases
{
    /// <summary>
    /// The most a page can carry and still be believed when it says it has
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Above one, not above nought: a page that found nothing still prints the
    /// term it was asked for, in its title and in its search box, and the term
    /// this plugin sends is a release name.
    /// </remarks>
    public const int Few = 2;

    /// <summary>What makes a name a release name rather than a sentence.</summary>
    /// <remarks>
    /// A resolution, a codec or a source. Deliberately not the episode number:
    /// <c>S03E06</c> is in the term that was searched for, so a page echoing the
    /// question back would look full of releases.
    /// </remarks>
    private static readonly Regex Markers = new(
        @"\b(?:2160p|1080p|720p|480p|x264|x265|h\.?264|h\.?265|hevc|xvid|divx|web-?dl|webrip|bluray|bdrip|hdtv|dvdrip)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>The furthest a name is followed out from its marker.</summary>
    private const int Reach = 60;

    /// <summary>The shortest a name can be and still be one.</summary>
    private const int Shortest = 8;

    /// <summary>How many releases <paramref name="body"/> appears to carry.</summary>
    /// <remarks>
    /// Grown outwards from each marker rather than matched whole. A regular
    /// expression for the name itself wants a lazy run either side of the
    /// marker with nothing to anchor it, which is the shape that backtracks;
    /// these captures run to seven hundred kilobytes, and a health tool that
    /// stops responding on one page reports on none of them. Walking out from
    /// the marker is linear and cannot do that.
    /// </remarks>
    public static int CountIn(string body)
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);

        foreach (Match marker in Markers.Matches(body))
        {
            int start = marker.Index;
            int end = marker.Index + marker.Length;

            while (start > 0 && marker.Index - start < Reach && IsNamePart(body[start - 1]))
            {
                start--;
            }

            while (end < body.Length && end - (marker.Index + marker.Length) < Reach && IsNamePart(body[end]))
            {
                end++;
            }

            string name = body[start..end].Trim();

            if (name.Length >= Shortest)
            {
                found.Add(name);
            }
        }

        return found.Count;
    }

    /// <summary>The characters a release name is made of.</summary>
    /// <remarks>
    /// The space is in here because half these sites print
    /// <c>Silo S03E07 1080p HEVC x265-MeGusta</c> with spaces where the scene
    /// name has dots. It costs the odd sentence being counted as one name,
    /// which under-counts and never over-counts.
    /// </remarks>
    private static bool IsNamePart(char character)
    {
        return char.IsAsciiLetterOrDigit(character)
            || character is '.' or '-' or '_' or '\'' or '[' or ']' or '(' or ')' or ' ';
    }
}
