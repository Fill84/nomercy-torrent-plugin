using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public static partial class ReleaseNameParser
{
    [GeneratedRegex(@"s(\d{1,2})e(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodePattern();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})x(\d{1,3})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex CrossPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})\s*episode\s*(\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VerbosePattern();

    [GeneratedRegex(@"s(\d{1,2})(?!e?\d)", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonPackPattern();

    [GeneratedRegex(@"season\s*(\d{1,2})", RegexOptions.IgnoreCase)]
    private static partial Regex VerboseSeasonPackPattern();

    private static Match? EarliestEpisodeMatch(string? title)
    {
        string text = title ?? string.Empty;
        Match? earliest = null;

        foreach (Regex pattern in new[] { SeasonEpisodePattern(), CrossPattern(), VerbosePattern() })
        {
            Match match = pattern.Match(text);
            if (match.Success && (earliest is null || match.Index < earliest.Index))
                earliest = match;
        }

        return earliest;
    }

    public static int? EpisodeMarkerIndex(string? title) => EarliestEpisodeMatch(title)?.Index;

    public static EpisodeSlot? ParseEpisode(string? title)
    {
        Match? match = EarliestEpisodeMatch(title);
        if (match is null)
            return null;

        return new EpisodeSlot(
            int.Parse(match.Groups[1].Value),
            int.Parse(match.Groups[2].Value)
        );
    }

    public static int? ParseSeasonPack(string? title)
    {
        if (ParseEpisode(title) is not null)
            return null;

        string text = title ?? string.Empty;

        Match verbose = VerboseSeasonPackPattern().Match(text);
        if (verbose.Success)
            return int.Parse(verbose.Groups[1].Value);

        Match compact = SeasonPackPattern().Match(text);
        return compact.Success ? int.Parse(compact.Groups[1].Value) : null;
    }
}
