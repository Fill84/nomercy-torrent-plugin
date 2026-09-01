using System.Text;
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
/// <para>
/// <strong>It under-counts and never over-counts.</strong> Two rows carrying the
/// same release under different ids are one name and are reported as one. That
/// is the safe direction: the number is read beside the rows the reader read,
/// and a page said to hold twice what it holds is a number an owner cannot act
/// on.
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

    /// <summary>The shortest a name can be and still be one.</summary>
    private const int Shortest = 8;

    /// <summary>
    /// The fewest words two windows must agree on before they are held to be
    /// one release seen twice.
    /// </summary>
    /// <remarks>
    /// Four, and never less than half of the shorter of the two. A page prints
    /// the same release in an <c>href</c> and again in the link's own text, and
    /// the two windows disagree at both ends — one carries the torrent's id and
    /// loses the tail, the other carries the tail and starts after a highlight
    /// tag. What they share is the middle of the name, and that is what says
    /// they are the same row.
    /// </remarks>
    private const int LeastAgreed = 4;

    /// <summary>How many releases <paramref name="body"/> appears to carry.</summary>
    /// <remarks>
    /// <para>
    /// Grown outwards from each marker rather than matched whole. A regular
    /// expression for the name itself wants a lazy run either side of the
    /// marker with nothing to anchor it, which is the shape that backtracks;
    /// these captures run to seven hundred kilobytes, and a health tool that
    /// stops responding on one page reports on none of them. Walking out from
    /// the marker is linear and cannot do that.
    /// </para>
    /// <para>
    /// <strong>To the whole run, not to a fixed reach.</strong> It used to stop
    /// sixty characters either side of the marker, and a name carrying three of
    /// them — <c>1080p</c>, <c>WEB-DL</c>, <c>H.264</c> — was grown three times
    /// from three starting points and stopped in three different places, which
    /// counted one release three times. Every marker in one name now grows to
    /// the same span, and the span is deduplicated by where it is before it is
    /// ever read. Nothing is lost by dropping the reach: a run ends at the
    /// first character a release name cannot hold, which is every quote, slash,
    /// comma and angle bracket on the page.
    /// </para>
    /// </remarks>
    public static int CountIn(string body)
    {
        HashSet<(int Start, int End)> spans = [];

        foreach (Match marker in Markers.Matches(body))
        {
            int start = marker.Index;
            int end = marker.Index + marker.Length;

            while (start > 0 && IsNamePart(body[start - 1]))
            {
                start--;
            }

            while (end < body.Length && IsNamePart(body[end]))
            {
                end++;
            }

            spans.Add((start, end));
        }

        HashSet<string> spelled = new(StringComparer.Ordinal);
        List<string[]> names = [];

        foreach ((int start, int end) in spans)
        {
            string[] words = Plain(body[start..end]);
            string name = string.Join(' ', words);

            if (name.Length >= Shortest && spelled.Add(name))
            {
                names.Add(words);
            }
        }

        // Longest first, so a window that caught part of a name joins the name
        // rather than founding a release of its own.
        names.Sort((left, right) => right.Length.CompareTo(left.Length));

        List<List<string[]>> releases = [];

        foreach (string[] name in names)
        {
            List<string[]>? already = releases.FirstOrDefault(release => release.Exists(one => Same(one, name)));

            if (already is null)
            {
                releases.Add([name]);
            }
            else
            {
                already.Add(name);
            }
        }

        return releases.Count;
    }

    /// <summary>
    /// Whether two windows are two views of one release.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One inside the other, or the tail of one being the head of the other.
    /// Both are what a page does to a release name it prints more than once:
    /// <c>href="/silo-s03e06-1080p-…-xupload-lektor-pl-21168576/"</c> against
    /// <c>…(S03E06) PL.Ai.1080p.…-XuploaD [Lektor PL AI] [ToAlien]</c>, where
    /// each carries an end the other does not and they agree on everything
    /// between.
    /// </para>
    /// <para>
    /// The tail-into-head rule cannot swallow two real releases. Every release
    /// name on a search page begins with the show, so for one to end where
    /// another begins it would have to end with the show's name.
    /// </para>
    /// </remarks>
    private static bool Same(string[] left, string[] right)
    {
        string one = $" {string.Join(' ', left)} ";
        string other = $" {string.Join(' ', right)} ";

        if (one.Contains(other, StringComparison.Ordinal) || other.Contains(one, StringComparison.Ordinal))
        {
            return true;
        }

        int shorter = Math.Min(left.Length, right.Length);
        int least = Math.Max(LeastAgreed, shorter / 2);

        for (int agreed = shorter; agreed >= least; agreed--)
        {
            if (Meets(left, right, agreed) || Meets(right, left, agreed))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the last <paramref name="agreed"/> words of one are the first of the other.</summary>
    private static bool Meets(string[] first, string[] second, int agreed)
    {
        for (int at = 0; at < agreed; at++)
        {
            if (!string.Equals(first[^(agreed - at)], second[at], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// One release name, spelled the one way, however the page spelled it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page carries the same release several times over — in the anchor's
    /// text, in its <c>href</c>, in a <c>title</c> or <c>data-tooltip</c>
    /// attribute — and a site that highlights the search term wraps parts of it
    /// in markup inside the attribute as well. Each of those is a different
    /// string and the set counted them as different releases.
    /// </para>
    /// <para>
    /// So only letters and digits survive, and everything else is a gap:
    /// <c>fqm[ettv]</c> and <c>fqm ettv</c> are one name. A word that is all
    /// digits goes with them, because the difference between a link and its own
    /// <c>href</c> is usually the torrent's id on the end of it. So do the words
    /// a URL is built from and a release name never is — <c>torrent</c>,
    /// <c>html</c> — because LimeTorrents ends every one of its addresses
    /// <c>-torrent.html</c>.
    /// </para>
    /// <para>
    /// On 31 August 2026 the un-normalised set reported TorrentBay as carrying
    /// thirty-one releases where its page held fourteen, and LimeTorrents
    /// forty-eight where it held seventeen — so the two sources whose readers
    /// were reading every row on the page were both flagged as broken, and the
    /// search went looking for a fault in the readers that was never there.
    /// </para>
    /// </remarks>
    private static string[] Plain(string name)
    {
        StringBuilder plain = new(name.Length);
        bool inside = false;
        bool spaced = true;

        foreach (char letter in System.Net.WebUtility.HtmlDecode(name))
        {
            if (letter == '<')
            {
                inside = true;

                continue;
            }

            if (letter == '>')
            {
                inside = false;

                continue;
            }

            if (inside)
            {
                continue;
            }

            if (!char.IsAsciiLetterOrDigit(letter))
            {
                if (!spaced)
                {
                    plain.Append(' ');
                    spaced = true;
                }

                continue;
            }

            plain.Append(char.ToLowerInvariant(letter));
            spaced = false;
        }

        return
        [
            .. plain
                .ToString()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(word => !word.All(char.IsAsciiDigit) && !Url.Contains(word)),
        ];
    }

    /// <summary>Words a URL is built from and a release name is not.</summary>
    private static readonly HashSet<string> Url = new(StringComparer.Ordinal)
    {
        "html",
        "htm",
        "php",
        "aspx",
        "torrent",
        "torrents",
        "magnet",
        "download",
        "downloads",
    };

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
