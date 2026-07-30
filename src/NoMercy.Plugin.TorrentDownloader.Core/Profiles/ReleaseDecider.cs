// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Profiles;

public record CandidateVerdict(
    ReleaseInfo Release,
    ParsedRelease Parsed,
    FilterVerdict Verdict,
    int Score
);

public class ReleaseDecider
{
    private readonly ReleaseFilter _filter = new();
    private readonly ReleaseScorer _scorer = new();

    public IReadOnlyList<CandidateVerdict> Evaluate(
        IEnumerable<ReleaseInfo> releases,
        FilterContext filter,
        ScoreContext score
    )
    {
        List<CandidateVerdict> verdicts = [];

        foreach (ReleaseInfo release in releases)
        {
            ParsedRelease parsed = ReleaseNameParser.Parse(release.Title);
            FilterVerdict verdict = _filter.Evaluate(release, parsed, filter);
            int value = verdict.Accepted ? _scorer.Score(release, parsed, score) : 0;
            verdicts.Add(new CandidateVerdict(release, parsed, verdict, value));
        }

        return verdicts
            .OrderByDescending(candidate => candidate.Verdict.Accepted)
            .ThenByDescending(candidate => candidate.Score)
            .ToList();
    }

    public CandidateVerdict? PickBest(
        IEnumerable<ReleaseInfo> releases,
        FilterContext filter,
        ScoreContext score
    )
    {
        CandidateVerdict? best = Evaluate(releases, filter, score).FirstOrDefault();
        return best is { Verdict.Accepted: true } ? best : null;
    }
}
