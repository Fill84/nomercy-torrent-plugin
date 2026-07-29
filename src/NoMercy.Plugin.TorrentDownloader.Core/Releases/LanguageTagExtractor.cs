using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class LanguageTagExtractor
{
    private const string English = "English";

    // "IT", "ES" and "DE" are deliberately absent. They are real language codes
    // and also common English substrings and release-group fragments, so
    // including them produces false positives on English releases.
    private static readonly Dictionary<string, string> Markers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENG"] = English,
        ["ENGLISH"] = English,
        ["FR"] = "French",
        ["VF"] = "French",
        ["VFF"] = "French",
        ["VFQ"] = "French",
        ["VFI"] = "French",
        ["VOSTFR"] = "French",
        ["FRENCH"] = "French",
        ["TRUEFRENCH"] = "French",
        ["GER"] = "German",
        ["GERMAN"] = "German",
        ["ITA"] = "Italian",
        ["ITALIAN"] = "Italian",
        ["SPANISH"] = "Spanish",
        ["ESP"] = "Spanish",
        ["ESPANOL"] = "Spanish",
        ["CASTELLANO"] = "Spanish",
        ["LATINO"] = "Spanish",
        ["NL"] = "Dutch",
        ["DUTCH"] = "Dutch",
        ["PL"] = "Polish",
        ["PLSUB"] = "Polish",
        ["POLISH"] = "Polish",
        ["KOR"] = "Korean",
        ["KOREAN"] = "Korean",
        ["JPN"] = "Japanese",
        ["JAPANESE"] = "Japanese",
        ["CHINESE"] = "Chinese",
        ["CANTONESE"] = "Chinese",
        ["MANDARIN"] = "Chinese",
        ["RUS"] = "Russian",
        ["RUSSIAN"] = "Russian",
        ["HINDI"] = "Hindi",
        ["TAMIL"] = "Tamil",
        ["TELUGU"] = "Telugu",
        ["SWEDISH"] = "Swedish",
        ["DANISH"] = "Danish",
        ["NORWEGIAN"] = "Norwegian",
        ["FINNISH"] = "Finnish",
        ["NORDIC"] = "Nordic",
        ["CZECH"] = "Czech",
        ["HUN"] = "Hungarian",
        ["HUNGARIAN"] = "Hungarian",
        ["TURKISH"] = "Turkish",
        ["POR"] = "Portuguese",
        ["PORTUGUESE"] = "Portuguese",
        ["PTBR"] = "Portuguese",
        ["GREEK"] = "Greek",
        ["HEBREW"] = "Hebrew",
        ["ARABIC"] = "Arabic",
        ["THAI"] = "Thai",
        ["VIETNAMESE"] = "Vietnamese",
        ["INDONESIAN"] = "Indonesian",
    };

    private static readonly (Regex Pattern, string Language)[] EpisodeWordHints =
    [
        (CapituloPattern(), "Spanish"),
        (EpisodioPattern(), "Italian"),
        (FolgePattern(), "German"),
        (StaffelPattern(), "German"),
        (OdcinekPattern(), "Polish"),
        (SeizoenPattern(), "Dutch"),
        (SaisonPattern(), "French"),
    ];

    [GeneratedRegex(@"[A-Za-z0-9]+")]
    private static partial Regex TokenPattern();

    [GeneratedRegex(@"\bdual([\s._-]?audio)?\b|\bmulti\d?\b|\bdubbed\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DualAudioPattern();

    // "cap" must be followed by a number so it never matches "Captain".
    [GeneratedRegex(@"\bcap\.?\s*\d|\bcapitulo\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CapituloPattern();

    [GeneratedRegex(@"\bepisodio\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EpisodioPattern();

    [GeneratedRegex(@"\bfolge\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FolgePattern();

    [GeneratedRegex(@"\bstaffel\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex StaffelPattern();

    [GeneratedRegex(@"\bodcinek\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OdcinekPattern();

    [GeneratedRegex(@"\bseizoen\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SeizoenPattern();

    [GeneratedRegex(@"\bsaison\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SaisonPattern();

    public static LanguageTags Extract(string? title)
    {
        string text = title ?? string.Empty;
        string scope = ScopeAfterNameBoundary(text);
        List<string> languages = [];

        foreach (Match token in TokenPattern().Matches(scope))
        {
            if (Markers.TryGetValue(token.Value, out string? language) && !languages.Contains(language))
                languages.Add(language);
        }

        foreach ((Regex pattern, string language) in EpisodeWordHints)
        {
            if (pattern.IsMatch(scope) && !languages.Contains(language))
                languages.Add(language);
        }

        if (languages.Count == 0)
            languages.Add(English);

        return new LanguageTags(languages, DualAudioPattern().IsMatch(scope));
    }

    // Tags follow the episode marker or season token; the show name precedes it. Scanning
    // the whole title makes a show called "Greek" or "Russian Doll" report that language and
    // then fail an English-required profile, so the episode (or season pack) is never grabbed.
    private static string ScopeAfterNameBoundary(string text) =>
        ReleaseNameParser.NameScopeBoundaryIndex(text) is int index ? text[index..] : text;
}
