// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record QualityLadder(IReadOnlyList<QualityDefinition> Ordered, string CutoffName)
{
    private readonly string _cutoffName = ValidateCutoffName(Ordered, CutoffName);

    // Validated in the init accessor (not a property initializer) so every construction path
    // runs the check: both `new QualityLadder(...)` and `with { CutoffName = ... }` invoke this
    // accessor. The compiler-generated copy constructor behind `with` copies backing fields
    // directly and does not call this accessor for properties a `with` expression leaves
    // untouched, but it does call it for CutoffName whenever a `with` sets that property.
    public string CutoffName
    {
        get => _cutoffName;
        init => _cutoffName = ValidateCutoffName(Ordered, value);
    }

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

    // This fallback is defence in depth, not a validation guarantee: it only protects against
    // a `with { Ordered = ... }` that drops the rung CutoffName still names (CutoffName's own
    // init accessor validates against the Ordered value at the time CutoffName is set, so it
    // cannot see a later, independent change to Ordered). In that case MeetsCutoff silently
    // returns false for every quality instead of throwing.
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
