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
    private const int MaxSoftScore = QualityStep - 1;

    public int Score(ReleaseInfo release, ParsedRelease parsed, ScoreContext context)
    {
        ReleaseProfile profile = context.Profile;
        int quality = Math.Max(profile.Quality.RankOf(parsed.Quality), 0);

        long soft = SceneScore(release, context);
        soft += GroupScore(parsed, profile);
        soft += TermScore(release.Title, profile);
        soft += FlagScore(parsed);
        soft += LanguageScore(parsed, profile);
        soft += CodecScore(parsed, profile);
        soft += release.IndexerPriority * IndexerPriorityScale;
        soft += (int)(Math.Log(1d + Math.Max(release.Seeders, 0)) * SeederScale);

        // Group and term scores are user-supplied and unbounded, and are multiplied by 100.
        // Clamping the whole soft total is what keeps "one quality step outranks every other
        // signal combined" true by construction rather than by convention.
        return quality * QualityStep + (int)Math.Clamp(soft, -MaxSoftScore, MaxSoftScore);
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

            if (TermMatcher.IsMatch(title, term.Pattern))
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
