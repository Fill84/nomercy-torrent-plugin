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
