using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// A release that was not taken, and why.
/// </summary>
/// <param name="Episode">Which gap it was offered for.</param>
/// <param name="Title">The release name, as it was announced.</param>
/// <param name="Source">
/// Which site it came from, or null when nothing was asked — a name the profile
/// refused never reached an indexer, and a page listing a site beside that line
/// would be telling the owner something untrue.
/// </param>
/// <param name="Reason">What to tell the owner, in words they can act on.</param>
public sealed record SkippedRelease(EpisodeKey Episode, string Title, string? Source, string Reason);

/// <summary>
/// What one cycle has decided so far.
/// </summary>
/// <remarks>
/// <para>
/// The rules that cannot be answered from one release alone: whether a season
/// has enough gaps to be worth a pack, which episodes a pack already taken has
/// settled, and everything that was refused. It is deliberately a per-cycle
/// object with state rather than a static rule — the answers change as the
/// cycle takes things.
/// </para>
/// <para>
/// It holds the real profile and the real filter. <strong>H1:</strong> every
/// test covering 0.3.4's seeder fault stubbed the profile out with a fake
/// chooser and passed while the plugin took nothing at all.
/// </para>
/// </remarks>
public sealed class Decisions
{
    private readonly Profile _profile;
    private readonly ReleaseFilter _filter;
    private readonly ReleaseDecider _decider;
    private readonly IReadOnlySet<string> _blacklisted;
    private readonly Dictionary<(int ShowId, int Season), List<EpisodeKey>> _gaps = [];
    private readonly HashSet<EpisodeKey> _settled = [];
    private readonly List<SkippedRelease> _skipped = [];

    public Decisions(Profile profile, IReadOnlyList<TrackedEpisode> missing, IReadOnlySet<string> blacklisted)
    {
        _profile = profile;
        _filter = new(profile);
        _decider = new(profile);
        _blacklisted = blacklisted;

        // The gaps by season, kept as the keys themselves rather than a count:
        // a pack that is taken has to settle each of them by name, and a count
        // cannot say which.
        foreach (TrackedEpisode episode in missing)
        {
            (int ShowId, int Season) season = (episode.Key.ShowId, episode.Key.Season);

            if (!_gaps.TryGetValue(season, out List<EpisodeKey>? keys))
            {
                keys = [];
                _gaps[season] = keys;
            }

            keys.Add(episode.Key);
        }
    }

    /// <summary>Everything refused this cycle, with the reason for each.</summary>
    /// <remarks>
    /// What the Skipped page renders, and what the control to allow one anyway
    /// acts on. Kept rather than logged and forgotten: "nothing worth taking"
    /// is the sentence that hid a release's worth of faults for a fortnight.
    /// </remarks>
    public IReadOnlyList<SkippedRelease> Skipped => _skipped;

    /// <summary>How many episodes of that season this cycle is looking for.</summary>
    public int GapsIn(int showId, int season)
    {
        return _gaps.TryGetValue((showId, season), out List<EpisodeKey>? keys) ? keys.Count : 0;
    }

    /// <summary>
    /// Whether something already taken this cycle answers for this episode.
    /// </summary>
    /// <remarks>
    /// An episode settled by a pack is not asked about again: that would be a
    /// search per episode for a file already on its way, and a second grab of
    /// the same season at the end of it.
    /// </remarks>
    public bool Settled(EpisodeKey episode)
    {
        return _settled.Contains(episode);
    }

    /// <summary>
    /// Whether this name is worth searching for, for this episode.
    /// </summary>
    /// <remarks>
    /// The profile's own rules, and then the one rule that needs the rest of
    /// the cycle: a pack is worth its bytes only when the season has enough
    /// gaps in it. Every refusal is recorded on the way out.
    /// </remarks>
    public Verdict JudgeName(ReleaseName name, TrackedEpisode episode)
    {
        Verdict verdict = _filter.JudgeName(name, episode, _blacklisted);

        if (!verdict.Accepted)
        {
            return Refuse(episode.Key, name.Original, null, verdict);
        }

        if (!name.IsPack)
        {
            return verdict;
        }

        int gaps = GapsIn(episode.Key.ShowId, episode.Key.Season);

        return gaps >= _profile.SeasonPackThreshold
            ? verdict
            : Refuse(
                episode.Key,
                name.Original,
                null,
                Verdict.No(
                    $"Season {episode.Key.Season} has {gaps} gaps and a pack is worth taking at {_profile.SeasonPackThreshold}."));
    }

    /// <summary>
    /// Takes the best copy of <paramref name="name"/>, or none, and remembers
    /// what that settles.
    /// </summary>
    public Decision Choose(TrackedEpisode episode, ReleaseName name, IReadOnlyList<ReleaseCopy> copies)
    {
        // What a site answered with is not necessarily what it was asked for.
        // A search puts a release name to seventeen indexers and each one
        // answers with whatever its own search engine thought; the copy's own
        // announced title has to pass the same rules the name did, or a row
        // for another programme entirely is taken because it happened to come
        // back well seeded.
        List<ReleaseCopy> forThisEpisode = [];

        foreach (ReleaseCopy copy in copies)
        {
            Verdict verdict = _filter.JudgeName(ReleaseName.Parse(copy.Title), episode, _blacklisted);

            if (verdict.Accepted)
            {
                forThisEpisode.Add(copy);
            }
            else
            {
                _skipped.Add(new(episode.Key, copy.Title, copy.Source, verdict.Reason));
            }
        }

        Decision decision = _decider.Decide(forThisEpisode, _blacklisted);

        foreach ((ReleaseCopy copy, string reason) in decision.Refused)
        {
            _skipped.Add(new(episode.Key, copy.Title, copy.Source, reason));
        }

        if (decision.Chosen is null)
        {
            return decision;
        }

        // A pack answers for every gap in the season it covers, and those are
        // the gaps this cycle knows about — which is the only list anything
        // here has. An episode of that season that is not missing needs
        // nothing, and one the library gains tomorrow is tomorrow's business.
        foreach (EpisodeKey key in CoveredBy(episode, name))
        {
            _settled.Add(key);
        }

        return decision;
    }

    /// <summary>
    /// Every gap one release answers for.
    /// </summary>
    /// <remarks>
    /// The episode it was found for, and the season's other gaps when it is a
    /// pack — the gaps this cycle knows about, which is the only list anything
    /// here has. An episode of that season that is not missing needs nothing,
    /// and one the library gains tomorrow is tomorrow's business.
    /// </remarks>
    public IReadOnlyList<EpisodeKey> CoveredBy(TrackedEpisode episode, ReleaseName name)
    {
        List<EpisodeKey> covered = [episode.Key];

        if (name.IsPack
            && name.Season is int season
            && _gaps.TryGetValue((episode.Key.ShowId, season), out List<EpisodeKey>? gaps))
        {
            covered.AddRange(gaps.Where(key => key != episode.Key));
        }

        return covered;
    }

    private Verdict Refuse(EpisodeKey episode, string title, string? source, Verdict verdict)
    {
        _skipped.Add(new(episode, title, source, verdict.Reason));

        return verdict;
    }
}
