using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// What a release name says about itself.
/// </summary>
/// <remarks>
/// Everything here is read from the name and nothing is guessed. A field the
/// name does not carry is null, which is a different thing from a field read as
/// nought — the profile can refuse an unknown codec and cannot refuse one it
/// invented.
/// </remarks>
/// <param name="Original">Exactly what the site printed, kept for the history and the pool.</param>
/// <param name="Title">The programme's name, as this release writes it.</param>
/// <param name="Season">The season, when the name carries one.</param>
/// <param name="Episode">The episode within the season.</param>
/// <param name="LastEpisode">The last episode of a range, for a name covering more than one.</param>
/// <param name="Absolute">
/// The episode counted from the start of the programme, which is how anime is
/// posted. Never the same field as <paramref name="Episode"/>: a show can be
/// searched for under both forms and they are different numbers.
/// </param>
/// <param name="LastAbsolute">The end of a batch's range.</param>
/// <param name="Version">A <c>v2</c> supersedes the <c>v1</c> of the same episode. One unless the name says otherwise.</param>
/// <param name="IsPack">Whether it covers a whole season or a run of episodes rather than one.</param>
/// <param name="Resolution">Written as the ladder writes it: <c>1080p</c>.</param>
/// <param name="Codec">The family, not the spelling — see <see cref="Parse"/>.</param>
/// <param name="Group">Who released it.</param>
/// <param name="Languages">Only the language claims the name really makes.</param>
public sealed record ReleaseName(
    string Original,
    string Title,
    int? Season = null,
    int? Episode = null,
    int? LastEpisode = null,
    int? Absolute = null,
    int? LastAbsolute = null,
    int Version = 1,
    bool IsPack = false,
    string? Resolution = null,
    string? Codec = null,
    string? Group = null,
    IReadOnlyList<string>? Languages = null)
{
    /// <summary>Never null: a name claiming no language claims none.</summary>
    public IReadOnlyList<string> Languages { get; init; } = Languages ?? [];

    /// <summary>
    /// Whether the name says which codec it is.
    /// </summary>
    /// <remarks>
    /// The profile refuses an untagged release when a codec is required, and
    /// that is not fussiness: an untagged release is where the unwanted codec
    /// hides.
    /// </remarks>
    public bool HasCodecTag => Codec is not null;

    /// <summary>The season tag, and a range after it when the name covers two.</summary>
    /// <remarks>
    /// <c>E(\d{1,4})</c>, not two digits: <c>One Piece S01E1173</c> is a real
    /// row on the Nyaa capture, and reading it as episode 11 would put the
    /// wrong file against the wrong slot.
    /// </remarks>
    private static readonly Regex SeasonAndEpisode = new(
        @"\bS(\d{1,2})E(\d{1,4})(?:\s*-\s*E?(\d{1,4})\b)?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A season with no episode after it: the whole season.</summary>
    private static readonly Regex SeasonAlone = new(
        @"\bS(\d{1,2})\b(?!E)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The anime separator and the number after it.
    /// </summary>
    /// <remarks>
    /// The separator is <c> - </c> with spaces, and what follows must be
    /// digits: an anime title is full of dashes — <em>Frieren - Beyond
    /// Journey's End</em> — and the number is the only thing that says which
    /// dash was the separator. The word boundary after the digits is what keeps
    /// <c>1080p</c> out: there is none between a digit and a letter, so
    /// <c>- 1080p</c> matches nothing at all while <c>- 137</c> matches. That is
    /// the whole of "137 is an episode and 1080 is not".
    /// </remarks>
    private static readonly Regex AnimeNumber = new(
        @"\s-\s+(\d{1,4})(?:v(\d))?(?:\s*~\s*(\d{1,4}))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The other way an absolute number is written: <c>EP1173</c>.
    /// </summary>
    /// <remarks>
    /// No document mentions it and the Nyaa capture is full of it —
    /// <c>[ToonsHub] One Piece EP1173 1080p NF WEB-DL</c> — with no separator
    /// anywhere in the name. Without this the episode is not read at all and
    /// the release sits in the pool answering for nothing.
    /// </remarks>
    private static readonly Regex AnimeEpisodeTag = new(
        @"\bEP\s?(\d{1,4})(?:v(\d))?\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A leading <c>[Group]</c>, which is where anime puts it.</summary>
    private static readonly Regex LeadingBracket = new(
        @"^\[([^\]]+)\]\s*",
        RegexOptions.Compiled);

    private static readonly Regex Extension = new(
        @"\.(mkv|mp4|avi|iso|ts|m4v|wmv|mov)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ResolutionTag = new(
        @"\b(\d{3,4})p\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Every spelling of a codec these sites use.
    /// </summary>
    /// <remarks>
    /// Including a bare <c>264</c>, because a site that turned the dots of
    /// <c>H.264</c> into spaces leaves the number standing on its own — EZTV
    /// prints exactly that. And <c>H.265</c> keeps its dot, which is why the
    /// dot is optional rather than absent.
    /// </remarks>
    private static readonly Regex CodecTag = new(
        @"\b(?:[xh]\.?26[45]|hevc|avc|xvid|divx|26[45])\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// A version, written on its own rather than against the number.
    /// </summary>
    /// <remarks>
    /// Preceded by a space or an opening bracket, so the <c>v</c> of a word is
    /// not a version. The form written against the number — <c>1172v2</c> — is
    /// read by <see cref="AnimeNumber"/>, since only there is it certain which
    /// number it belongs to.
    /// </remarks>
    private static readonly Regex StandaloneVersion = new(
        @"(?<=[\s\[(])v(\d)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>The words that say a name covers a run of episodes.</summary>
    private static readonly Regex PackWord = new(
        @"\b(?:batch|complete)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The language claims a name makes, and only those.
    /// </summary>
    /// <remarks>
    /// A closed list, from docs/04-domain.md. Anything wider reads the words of
    /// a title as claims about it: <em>Greek</em> is a programme, and the page
    /// it was captured from carries both that show and a dozen others with
    /// Greek subtitles. The list has no Greek in it, so neither can be
    /// mistaken for the other.
    /// </remarks>
    private static readonly (string Name, Regex Pattern)[] LanguageClaims =
    [
        // Not when it is Multi-Subs: subtitles in several languages are not the
        // release being in several languages, and "MULTi" alone means the audio.
        ("multi", new(@"\bmulti\b(?!\s*-?\s*sub)", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("vostfr", new(@"\bvostfr\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("dual audio", new(@"\bdual[\s._-]?audio\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("multiple subtitle", new(@"\bmultiple[\s._-]?subtitles?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
    ];

    /// <summary>
    /// Reads <paramref name="name"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two grammars, tried in the order that can tell them apart. A season tag
    /// is unambiguous, so it wins: anime is posted scene-styled as often as
    /// not, and <c>Frieren.Beyond.Journey.s.End.S01E13</c> is a season and an
    /// episode however it was numbered elsewhere. Only a name with no season
    /// tag is read as an absolute number, and only a name with neither is read
    /// as a whole season.
    /// </para>
    /// <para>
    /// The codec is answered as a family — <c>h264</c>, <c>h265</c>,
    /// <c>xvid</c>, <c>divx</c> — because the seven spellings of one codec are
    /// one codec, and a rule written against a spelling refuses six copies of
    /// the thing it was asked to accept.
    /// </para>
    /// </remarks>
    public static ReleaseName Parse(string name)
    {
        string work = name.Trim();
        string? group = null;

        Match bracket = LeadingBracket.Match(work);

        if (bracket.Success)
        {
            group = bracket.Groups[1].Value.Trim();
            work = work[bracket.Length..];
        }

        string withoutExtension = Extension.Replace(work, string.Empty);

        group ??= TrailingGroup(withoutExtension);

        Match slot = SeasonAndEpisode.Match(withoutExtension);
        Match absolute = slot.Success ? Match.Empty : AnimeNumber.Match(withoutExtension);

        if (!slot.Success && !absolute.Success)
        {
            absolute = AnimeEpisodeTag.Match(withoutExtension);
        }

        Match season = slot.Success || absolute.Success ? Match.Empty : SeasonAlone.Match(withoutExtension);

        int cut = slot.Success ? slot.Index
            : absolute.Success ? absolute.Index
            : season.Success ? season.Index
            : withoutExtension.Length;

        Match codec = CodecTag.Match(withoutExtension);
        Match resolution = ResolutionTag.Match(withoutExtension);
        Match version = StandaloneVersion.Match(withoutExtension);

        bool range = absolute.Success && absolute.Groups[3].Success;

        return new(
            name,
            Words(withoutExtension[..cut]),
            slot.Success ? Number(slot.Groups[1]) : season.Success ? Number(season.Groups[1]) : null,
            slot.Success ? Number(slot.Groups[2]) : null,
            slot.Success ? Number(slot.Groups[3]) : null,
            absolute.Success ? Number(absolute.Groups[1]) : null,
            range ? Number(absolute.Groups[3]) : null,
            absolute.Success && absolute.Groups[2].Success
                ? Number(absolute.Groups[2]) ?? 1
                : version.Success ? Number(version.Groups[1]) ?? 1 : 1,
            // A season with no episode is the season. A range is every episode
            // in it. And the words only when no single episode was found, or a
            // programme called The Complete History of Anything is a pack.
            season.Success || range || (!slot.Success && !absolute.Success && PackWord.IsMatch(withoutExtension))
                || (absolute.Success && PackWord.IsMatch(withoutExtension)),
            resolution.Success ? $"{resolution.Groups[1].Value}p" : null,
            codec.Success ? Family(codec.Value) : null,
            group,
            [.. LanguageClaims.Where(claim => claim.Pattern.IsMatch(withoutExtension)).Select(claim => claim.Name)]);
    }

    /// <summary>
    /// The group at the end of a scene name.
    /// </summary>
    /// <remarks>
    /// After the last dash, and it has to run to the end of the name: a scene
    /// title is full of dashes and so are the tags, so <c>WEB-DL</c> in the
    /// middle of a name would otherwise answer "DL". It carries no dot and no
    /// space, which is what tells a group from the tail of a name that happens
    /// to follow a dash.
    /// </remarks>
    private static string? TrailingGroup(string name)
    {
        int dash = name.LastIndexOf('-');

        if (dash < 0 || dash == name.Length - 1)
        {
            return null;
        }

        string tail = name[(dash + 1)..];

        return tail.Contains('.', StringComparison.Ordinal)
               || tail.Any(char.IsWhiteSpace)
            ? null
            : tail;
    }

    /// <summary>The codec family, whichever of its spellings the name used.</summary>
    private static string Family(string written)
    {
        string tag = written.ToLowerInvariant();

        if (tag.Contains("265", StringComparison.Ordinal) || tag == "hevc")
        {
            return "h265";
        }

        if (tag.Contains("264", StringComparison.Ordinal) || tag == "avc")
        {
            return "h264";
        }

        return tag;
    }

    /// <summary>The title as words, with the separators a site writes it with gone.</summary>
    private static string Words(string title)
    {
        return Spaces
            .Replace(title.Replace('.', ' ').Replace('_', ' '), " ")
            .Trim()
            .Trim('-')
            .Trim();
    }

    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    private static int? Number(Group captured)
    {
        return captured.Success && int.TryParse(captured.Value, out int number) ? number : null;
    }
}
