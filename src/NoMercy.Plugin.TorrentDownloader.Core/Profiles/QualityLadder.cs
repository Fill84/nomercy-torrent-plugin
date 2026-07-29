using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityLadder(IReadOnlyList<QualityDefinition> Ordered, string CutoffName)
{
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
