using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Naming;

/// <summary>
/// Whether a release is for the show that was asked about.
/// </summary>
/// <remarks>
/// Begins with, never contains. <em>A Bloody Lucky Day</em> contains
/// <em>Lucky</em> and is a different programme, and the library holds a show
/// called <em>Lucky</em>.
/// </remarks>
public static class TitleMatcher
{
    private static readonly Regex Punctuation = new(
        @"[^\p{L}\p{N}]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether <paramref name="releaseTitle"/> is a release of
    /// <paramref name="showTitle"/>.
    /// </summary>
    /// <remarks>
    /// Counted in words rather than in letters. <em>Silos</em> begins with the
    /// letters of <em>Silo</em> and is not that show — the LimeTorrents capture
    /// carries a row titled <c>Silos / Silo (2023–)</c>, so this is a real row
    /// and not a hypothetical one.
    /// </remarks>
    public static bool Matches(string releaseTitle, string showTitle)
    {
        string[] release = Words(releaseTitle);
        string[] show = Words(showTitle);

        // Nothing matches nothing. A show with no title is a fault upstream,
        // and answering true would file every release in the pool under it.
        return show.Length != 0
               && release.Length >= show.Length
               && release.Take(show.Length).SequenceEqual(show, StringComparer.Ordinal);
    }

    /// <summary>
    /// A title as one string, normalised the same way a comparison normalises
    /// it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For the pool key, which two stages that never meet have to spell
    /// identically. Sharing the folding rather than repeating it is the point:
    /// a second copy that handled accents differently would file a name under a
    /// key nothing ever looks up.
    /// </para>
    /// <para>
    /// The words are run together here, and that is the difference from
    /// <see cref="Matches"/>. One Nyaa page carries the same episode as
    /// <c>Frieren.Beyond.Journey.s.End</c>, <c>Frieren Beyond Journeys End</c>
    /// and <c>Frieren- Beyond Journey's End</c>: the apostrophe is a space in
    /// one, a letter's worth of nothing in another and a dot in the third, so
    /// any key that keeps the gaps files three names for one episode. Matching
    /// keeps its words, because there the gaps are what tell <em>Silo</em> from
    /// <em>Silos</em>.
    /// </para>
    /// </remarks>
    public static string Normalised(string title)
    {
        return string.Concat(Words(title));
    }

    /// <summary>
    /// A title as words: lowercase, accents folded, punctuation gone, runs
    /// collapsed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A letter is anything a language calls one, so a diacritic is never cut
    /// into fragments — which is what stopped <em>Pokémon</em> matching itself
    /// in 0.3.4.
    /// </para>
    /// <para>
    /// And the accent is then folded away, because one Nyaa row carries the
    /// same programme spelled both ways — <c>Pokémon Horizons: The Series</c>
    /// with the accent and <c>Pokemon (2023)</c> without — so insisting on the
    /// accent refuses a release of exactly the show that was asked for.
    /// </para>
    /// </remarks>
    private static string[] Words(string title)
    {
        string folded = new(title
            .ToLowerInvariant()
            .Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            .ToArray());

        return Punctuation
            .Replace(folded, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}
