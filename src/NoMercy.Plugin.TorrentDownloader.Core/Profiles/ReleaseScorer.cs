using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public class ReleaseScorer
{
    private const int QualityStep = 10_000;
    private const int SceneMatchBonus = 5_000;
    private const int GroupScoreScale = 100;
    private const int TermScoreScale = 100;
    private const int DualAudioBonus = 750;
    private const int ProperBonus = 500;
    private const int RepackBonus = 500;
    private const int PreferredLanguageBonus = 500;
    private const int CodecMatchBonus = 250;
    private const int IndexerPriorityScale = 50;
    private const double SeederScale = 25d;

    public int Score(ReleaseInfo release, ParsedRelease parsed, ScoreContext context)
    {
        ReleaseProfile profile = context.Profile;
        int score = Math.Max(profile.Quality.RankOf(parsed.Quality), 0) * QualityStep;

        score += SceneScore(release, context);
        score += GroupScore(parsed, profile);
        score += TermScore(release.Title, profile);
        score += FlagScore(parsed);
        score += LanguageScore(parsed, profile);
        score += CodecScore(parsed, profile);
        score += release.IndexerPriority * IndexerPriorityScale;
        score += (int)(Math.Log(1d + Math.Max(release.Seeders, 0)) * SeederScale);

        return score;
    }

    private static int SceneScore(ReleaseInfo release, ScoreContext context)
    {
        if (context.AnnouncedSceneTitle is not string announced)
            return 0;

        return TitleMatcher.Normalize(release.Title) == TitleMatcher.Normalize(announced)
            ? SceneMatchBonus
            : 0;
    }

    private static int GroupScore(ParsedRelease parsed, ReleaseProfile profile)
    {
        if (parsed.ReleaseGroup is not string group)
            return 0;

        int total = 0;
        foreach (GroupPreference preference in profile.PreferredGroups)
        {
            if (string.Equals(preference.Group, group, StringComparison.OrdinalIgnoreCase))
                total += preference.Score * GroupScoreScale;
        }

        return total;
    }

    private static int TermScore(string title, ReleaseProfile profile)
    {
        int total = 0;
        foreach (TermRule term in profile.Terms)
        {
            if (term.Kind != TermKind.Preferred)
                continue;

            if (Regex.IsMatch(title, term.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
                total += term.Score * TermScoreScale;
        }

        return total;
    }

    private static int FlagScore(ParsedRelease parsed) =>
        (parsed.IsProper ? ProperBonus : 0) + (parsed.IsRepack ? RepackBonus : 0);

    private static int LanguageScore(ParsedRelease parsed, ReleaseProfile profile)
    {
        int total = 0;

        if (profile.Language.RequireDualAudio && parsed.IsDualAudio)
            total += DualAudioBonus;

        foreach (string preferred in profile.Language.Preferred)
        {
            if (parsed.Languages.Contains(preferred, StringComparer.OrdinalIgnoreCase))
                total += PreferredLanguageBonus;
        }

        return total;
    }

    private static int CodecScore(ParsedRelease parsed, ReleaseProfile profile) =>
        profile.Codec != VideoCodec.Unknown && parsed.Codec == profile.Codec ? CodecMatchBonus : 0;
}
