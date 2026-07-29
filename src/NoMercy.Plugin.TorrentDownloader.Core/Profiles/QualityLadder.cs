using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityLadder(IReadOnlyList<QualityDefinition> Ordered, string CutoffName)
{
    // Validated eagerly so a typo'd cutoff fails at profile-load time with the bad value
    // and the available rungs named, instead of silently disabling MeetsCutoff for every
    // quality via CutoffRank's int.MaxValue fallback.
    public string CutoffName { get; init; } = ValidateCutoffName(Ordered, CutoffName);

    private static string ValidateCutoffName(IReadOnlyList<QualityDefinition> ordered, string cutoffName)
    {
        if (ordered.Any(definition => string.Equals(definition.Name, cutoffName, StringComparison.OrdinalIgnoreCase)))
            return cutoffName;

        string available = string.Join(", ", ordered.Select(definition => definition.Name));
        throw new ArgumentException(
            $"CutoffName \"{cutoffName}\" matches no rung on the ladder. Available rungs: {available}",
            nameof(cutoffName)
        );
    }

    public int RankOf(Quality quality)
    {
        int specific = -1;
        int agnostic = -1;

        for (int index = 0; index < Ordered.Count; index++)
        {
            QualityDefinition definition = Ordered[index];
            if (!definition.Matches(quality))
                continue;

            if (definition.IsSourceSpecific)
                specific = index;
            else if (agnostic < 0)
                agnostic = index;
        }

        return specific >= 0 ? specific : agnostic;
    }

    public int CutoffRank
    {
        get
        {
            for (int index = 0; index < Ordered.Count; index++)
            {
                if (string.Equals(Ordered[index].Name, CutoffName, StringComparison.OrdinalIgnoreCase))
                    return index;
            }

            return int.MaxValue;
        }
    }

    public bool IsAllowed(Quality quality) => RankOf(quality) >= 0;

    public bool MeetsCutoff(Quality quality)
    {
        int rank = RankOf(quality);
        return rank >= 0 && rank >= CutoffRank;
    }
}
