using NoMercy.Plugin.TorrentDownloader.Core.Domain;

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
    /// Seeders first, then the site's own rating <strong>descending</strong>.
    /// 0.3.4 had the second one inverted and picked the worst-rated site every
    /// time two copies were level on seeders, which is most of the time.
    /// </remarks>
    public Decision Decide(IReadOnlyList<ReleaseCopy> copies, IReadOnlySet<string> blacklisted)
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
                // A copy whose site does not publish a count sorts below one
                // that does and has some: it might be well seeded and nothing
                // says so.
                .OrderByDescending(copy => copy.Seeders ?? 0)
                .ThenByDescending(copy => copy.Priority),
        ];

        return new(ranked, refused);
    }
}
