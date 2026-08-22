using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Which copies are worth taking, best first, and what became of the rest.
/// </summary>
/// <param name="Ranked">
/// Every acceptable copy in the order it is worth trying. A list rather than
/// one copy, because the best copy is not always reachable: on 22 August 2026
/// the highest-seeded copy of Silo S03E08 came from a site whose magnet lives
/// behind a signed request this plugin does not make, the cycle followed it,
/// found no torrent and stopped — with a copy of the same episode from another
/// site sitting unexamined and the episode reported as unavailable.
/// </param>
/// <param name="Refused">
/// Every copy that was not acceptable and why. Kept rather than discarded: the
/// Skipped page renders exactly this, and the owner can allow one anyway.
/// </param>
public sealed record Decision(
    IReadOnlyList<ReleaseCopy> Ranked,
    IReadOnlyList<(ReleaseCopy Copy, string Reason)> Refused)
{
    /// <summary>The one to try first, or null when nothing was acceptable.</summary>
    public ReleaseCopy? Chosen => Ranked.Count > 0 ? Ranked[0] : null;
}

/// <summary>
/// Chooses one copy from what the indexers answered, or none.
/// </summary>
/// <remarks>
/// The real profile does the judging, here as everywhere. <strong>H1:</strong>
/// every test covering 0.3.4's seeder fault stubbed the profile out with a fake
/// chooser and passed while the plugin took nothing at all, so a fake chooser
/// is only ever for a test about plumbing and never for one about a decision.
/// </remarks>
public sealed class ReleaseDecider(Profile profile)
{
    private readonly ReleaseFilter _filter = new(profile);

    /// <summary>
    /// Judges every copy and ranks the survivors, best first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The release the name databases published for this episode first, then
    /// seeders, then the site's own rating <strong>descending</strong>. 0.3.4
    /// had the last one inverted and picked the worst-rated site every time two
    /// copies were level on seeders, which is most of the time.
    /// </para>
    /// <para>
    /// The scene name coming first is the whole point of the pool. SceneSource
    /// and PreDB publish what a release is called minutes after it lands, and a
    /// copy whose title <em>is</em> that name is the strongest evidence there is
    /// that it is the genuine thing. Ranking on a seeder count alone hands the
    /// episode to whichever re-encode a crowd happened to gather around.
    /// </para>
    /// </remarks>
    /// <param name="copies">What the indexers answered with.</param>
    /// <param name="blacklisted">Keys already refused.</param>
    /// <param name="known">
    /// The release names a name database published for this episode, normalised.
    /// A copy that <em>is</em> one of them wins outright.
    /// </param>
    public Decision Decide(
        IReadOnlyList<ReleaseCopy> copies,
        IReadOnlySet<string> blacklisted,
        IReadOnlySet<string>? known = null)
    {
        List<(ReleaseCopy Copy, string Reason)> refused = [];
        List<ReleaseCopy> acceptable = [];

        foreach (ReleaseCopy copy in copies)
        {
            Verdict verdict = _filter.JudgeCopy(copy, blacklisted);

            if (verdict.Accepted)
            {
                acceptable.Add(copy);
            }
            else
            {
                refused.Add((copy, verdict.Reason));
            }
        }

        ReleaseCopy[] ranked =
        [
            .. acceptable
                // The release a name database published for this episode, if
                // one of them is here. It wins outright and seeders do not get
                // a say: for Silo S03E04 an x265 re-encode is seeded by 2,898
                // and the scene release by 1,774, so on a count alone the
                // re-encode wins a contest it should never have been in.
                .OrderByDescending(copy => known is not null
                                           && known.Contains(TitleMatcher.Release(copy.Title)))

                // A copy whose site does not publish a count sorts below one
                // that does and has some: it might be well seeded and nothing
                // says so.
                .ThenByDescending(copy => copy.Seeders ?? 0)
                .ThenByDescending(copy => copy.Priority),
        ];

        return new(ranked, refused);
    }
}
