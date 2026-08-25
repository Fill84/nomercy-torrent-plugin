using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// What became of one episode this cycle.
/// </summary>
/// <param name="Episode">Which gap.</param>
/// <param name="Release">The release that was taken, or would have been, or null when there was none.</param>
/// <param name="Source">The site it came from.</param>
/// <param name="Seeders">How many are serving it, or null when the site did not say.</param>
/// <param name="HandedOver">Whether the torrent client was actually given it.</param>
/// <param name="Detail">
/// What to tell the owner, in words. Never "nothing worth taking" — that is the
/// sentence that hid a release's worth of faults for a fortnight.
/// </param>
public sealed record EpisodeOutcome(
    EpisodeKey Episode,
    string? Release,
    string? Source,
    int? Seeders,
    bool HandedOver,
    string Detail)
{
    /// <summary>What the client calls it, when it took it.</summary>
    public string? InfoHash { get; init; }

    /// <summary>
    /// What it was taken from.
    /// </summary>
    /// <remarks>
    /// Kept after the client has it, so a torrent the client has forgotten is
    /// re-added rather than searched for and downloaded all over again.
    /// </remarks>
    public string? Magnet { get; init; }

    /// <summary>
    /// Whether an indexer was actually asked about this episode.
    /// </summary>
    /// <remarks>
    /// What makes a search attempt an attempt. An episode nothing could be
    /// asked about, and one settled by a pack taken earlier, have not been
    /// looked for — counting either would spend the owner's
    /// <c>MaxSearchAttempts</c> on work nobody did and give up on an episode
    /// that was never searched for once.
    /// </remarks>
    public bool Searched { get; init; }

    /// <summary>
    /// What this copy was chosen ahead of, and by how much.
    /// </summary>
    /// <remarks>
    /// A copy that is acceptable and not taken is recorded nowhere, so "why
    /// did that one win" could only be answered by running the cycle again and
    /// watching. On 22 August 2026 the owner asked it of a real decision and
    /// nothing in the plugin could say. The runners-up travel with the outcome
    /// so the History page can.
    /// </remarks>
    public string? Considered { get; init; }

    /// <summary>
    /// Every gap this release answers for: one for an ordinary release, the
    /// season's remaining gaps for a pack.
    /// </summary>
    /// <remarks>
    /// It travels with the grab because a pack that fails has to put all of
    /// them back to missing at once, and nothing downstream can work out which
    /// they were.
    /// </remarks>
    public IReadOnlyList<EpisodeKey> Covers { get; init; } = [];
}

/// <summary>
/// What one cycle is allowed to do, and with what.
/// </summary>
/// <param name="Profile">What the owner will accept.</param>
/// <param name="Blacklisted">Keys already refused, read once for the cycle.</param>
/// <param name="DryRun">
/// Decide everything and hand nothing over. It says what to do with a decision
/// rather than what makes one acceptable, which is why it is not on the
/// profile.
/// </param>
/// <param name="IncompleteFolder">Where a download lands while it runs.</param>
public sealed record CycleOptions(
    Profile Profile,
    IReadOnlySet<string> Blacklisted,
    bool DryRun,
    string IncompleteFolder)
{
    /// <summary>The owner's own tracker list, added to every grab.</summary>
    /// <remarks>
    /// It ships empty and that is the owner's decision: only the trackers a
    /// source's own magnet supplied travel with a grab, so nothing announces
    /// what is being downloaded to a host the owner never agreed to.
    /// </remarks>
    public IReadOnlyList<string> DefaultTrackers { get; init; } = [];
}

/// <summary>Everything one cycle decided.</summary>
/// <param name="Outcomes">One per episode looked at, in the order they were looked at.</param>
/// <param name="Skipped">Every release refused, with its reason, for the Skipped page.</param>
public sealed record CycleReport(IReadOnlyList<EpisodeOutcome> Outcomes, IReadOnlyList<SkippedRelease> Skipped)
{
    /// <summary>
    /// Every tracker this cycle came across, on any copy of anything.
    /// </summary>
    /// <remarks>
    /// From every copy and not only the ones taken: the owner's decision of
    /// 20 August 2026 is that the default list is everything the plugin meets,
    /// and a tracker on a release that was refused is serving the same swarm as
    /// the one that was taken. What is safe to keep is
    /// <see cref="TrackerBook"/>'s business, not this record's.
    /// </remarks>
    public IReadOnlyList<string> Trackers { get; init; } = [];
}

/// <summary>
/// The whole chain for one pass: names, search, decision, grab.
/// </summary>
/// <remarks>
/// <para>
/// One decision per episode, reported with the release, the site, the seeder
/// count and the reason for any refusal — because the thing 0.3.4 could not
/// answer was "what happened to this episode", and every fault it shipped hid
/// behind that.
/// </para>
/// <para>
/// Nothing here talks to a site directly: the stages do that, each gated per
/// host. This decides the order, keeps the cycle's own state, and hands over
/// what was chosen.
/// </para>
/// </remarks>
public sealed class SearchCycle(
    NameResolve names,
    Find find,
    IActivityJournal journal,
    Grab? grab = null,
    ICycleJournal? written = null)
{
    /// <param name="missing">The gaps to look at, in whatever order they arrive.</param>
    /// <param name="options">What the owner will accept, and what to do with what is found.</param>
    /// <param name="ct">The plugin's own lifetime, never a caller's request.</param>
    public async Task<CycleReport> RunAsync(
        IReadOnlyList<TrackedEpisode> missing,
        CycleOptions options,
        CancellationToken ct)
    {
        // The order the Queue page shows, so the page states what the plugin
        // will do rather than guessing at it.
        TrackedEpisode[] queue = [.. QueueOrder.Order(missing)];

        Decisions decisions = new(options.Profile, queue, options.Blacklisted);

        IReadOnlyList<ResolvedNames> resolved = await names.ResolveAsync(queue, ct);

        Dictionary<EpisodeKey, IReadOnlyList<string>> byEpisode =
            resolved.ToDictionary(one => one.Episode, one => one.Titles);

        List<EpisodeOutcome> outcomes = [];

        // Every tracker anything published this cycle. Kept in the order they
        // were met so the owner's settings do not churn.
        List<string> trackers = [];

        // Every copy any search this cycle answered with, whichever gap it was
        // asked for. A site asked about one episode answers with the whole
        // programme, and on 22 August 2026 four 1080p copies of Silo S03E04 to
        // S03E07 - every one of them an episode the library was missing - came
        // back from a search for S03E08 and were thrown away. They are kept so
        // the gap they do answer for can have them without asking again.
        List<ReleaseCopy> answered = [];

        // What each term this cycle has already been answered with. The
        // programme's own name is a term every gap of that programme falls
        // through to, so eight gaps asked every indexer the identical question
        // eight times - and apibay, which rate-limits hard, answered 429 to the
        // ninth. It saves the request and never the decision: what comes back
        // still goes to every gap it answers for.
        //
        // For this cycle and no longer. Both this and the copies below are
        // thrown away when the run ends, so the next one asks again from
        // nothing - which is what an episode that aired an hour ago needs, and
        // is why neither of them is written down anywhere.
        Dictionary<string, IReadOnlyList<ReleaseCopy>> asked = new(StringComparer.OrdinalIgnoreCase);

        // One episode at a time, because the decisions of each are part of the
        // state of the next: a pack taken for season three settles the rest of
        // season three, and running them together would have two of them grab
        // the same season.
        foreach (TrackedEpisode episode in queue)
        {
            ct.ThrowIfCancellationRequested();

            // What was refused before this episode, so what is refused for it
            // can be told apart and written with it.
            int refusedBefore = decisions.Skipped.Count;

            EpisodeOutcome outcome = await LookAsync(
                episode,
                byEpisode.GetValueOrDefault(episode.Key, []),
                decisions,
                options,
                trackers,
                answered,
                asked,
                ct);

            outcomes.Add(outcome);

            // Written now rather than when the whole queue is done. Over
            // twenty-eight gaps that is half an hour in which the pages say
            // nothing, and a run stopped in that time threw away everything it
            // had decided.
            if (written is not null)
            {
                await written.DecidedAsync(
                    outcome,
                    [.. decisions.Skipped.Skip(refusedBefore)],
                    ct);
            }
        }

        return new(outcomes, decisions.Skipped) { Trackers = trackers };
    }

    private async Task<EpisodeOutcome> LookAsync(
        TrackedEpisode episode,
        IReadOnlyList<string> candidates,
        Decisions decisions,
        CycleOptions options,
        List<string> trackers,
        List<ReleaseCopy> answered,
        Dictionary<string, IReadOnlyList<ReleaseCopy>> asked,
        CancellationToken ct)
    {
        string subject = $"{episode.ShowTitle} {episode.Key}";

        if (decisions.Settled(episode.Key))
        {
            // Already answered for by something taken earlier in this cycle.
            // Asking again is a search for a file already on its way.
            return new(episode.Key, null, null, null, false, "settled by a pack taken earlier this cycle");
        }

        journal.Started(ActivityStage.Decide, subject, $"{candidates.Count} names");

        // Why the client would not have a copy that was otherwise worth
        // taking. Kept, because "nothing anybody is serving is worth taking" is
        // the wrong answer when the truth is that the disk is full - and the
        // owner can act on one of those two and not on the other.
        List<string> refused = [];

        try
        {
            // What earlier searches this cycle have already turned up. A
            // candidate and never an answer: it is added to what this
            // episode's own search brings back, and decides nothing on its
            // own.
            //
            // It used to decide. The cycle tried this stack first and took the
            // first acceptable thing in it, so an episode could be settled by
            // a leftover from another episode's search without one indexer
            // being asked about it - and on 22 August 2026 Sugar S02E08 was
            // settled that way while the copy the owner wanted, top of the
            // page on two sites at four hundred and eighty seeders, was never
            // fetched at all.
            List<ReleaseCopy> gathered = [.. answered];

            // The programme's own answer, already paid for. One search of
            // "Silo" carries every gap of every season of it, and the term is
            // asked once a cycle - so the second gap of a season costs nothing
            // where the first one paid.
            //
            bool searched = false;

            foreach ((string term, bool shelf) in Terms(episode, candidates, options.Profile))
            {
                if (!asked.TryGetValue(term, out IReadOnlyList<ReleaseCopy>? copies))
                {
                    copies = await find.SearchAsync(term, episode.Kind, ct);
                    asked[term] = copies;

                    // Every copy, taken or not: a tracker on a release the
                    // profile refused is serving the same swarm as the one it
                    // accepted.
                    trackers.AddRange(copies.SelectMany(copy => copy.Trackers));

                    // Only what a shelf answered is shared. A season or a
                    // programme was asked for the whole of itself, so every gap
                    // in it is entitled to the answer; an episode's own search
                    // was asked about that episode, and letting its leftovers
                    // settle another gap is how Sugar S02E08 came to be decided
                    // by a stray row while the release everybody was seeding
                    // went unfetched.
                    if (shelf)
                    {
                        answered.AddRange(copies);
                    }
                }

                searched = true;

                gathered.AddRange(copies);

            }

            // Every name, on every indexer, before anything is taken. A site
            // only answers about the name it was asked, so an indexer holding
            // the release under a spelling the first term did not use is asked
            // and still never finds it — and its trackers never reach the
            // magnet. Two Lioness episodes sat at "fetching metadata" with no
            // peer and no seed while the same release seeded through trackers
            // only a later name would have found.
            //
            // The cost is names times indexers rather than indexers, and it is
            // the per-host gate that keeps that civil: every request to a site
            // waits its turn behind that site's own pace, whoever asked for it.
            if (await TakeAsync(episode, gathered, decisions, options, subject, trackers, refused, candidates, ct)
                is EpisodeOutcome taken)
            {
                return taken with { Searched = searched };
            }

            journal.Finished(ActivityStage.Decide, subject, "nobody is serving an acceptable copy");

            return new(
                episode.Key,
                null,
                null,
                null,
                false,
                refused.Count > 0
                    ? refused[^1]
                    : searched
                        ? "every indexer was asked, and nothing anybody is serving is worth taking"
                        : "there was nothing to ask an indexer")
            {
                Searched = searched,
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Decide, subject, exception.Message);

            return new(episode.Key, null, null, null, false, exception.Message);
        }
    }

    /// <summary>
    /// Takes the best copy of this episode anybody is serving, or none.
    /// </summary>
    /// <remarks>
    /// Down the ranking rather than only at the top of it. The best copy is the
    /// one with the most seeders, and that is often the one hardest to reach:
    /// on 22 August 2026 the highest-seeded copy of Silo S03E08 came from a
    /// site whose magnet lives behind a signed request this plugin does not
    /// make. The cycle followed it, found no torrent and stopped, with a copy
    /// of the same episode from another site sitting unexamined and the
    /// episode reported as though nobody were serving it.
    /// </remarks>
    private async Task<EpisodeOutcome?> TakeAsync(
        TrackedEpisode episode,
        IReadOnlyList<ReleaseCopy> copies,
        Decisions decisions,
        CycleOptions options,
        string subject,
        List<string> trackers,
        List<string> refused,
        IReadOnlyList<string> known,
        CancellationToken ct)
    {
        if (copies.Count == 0)
        {
            return null;
        }

        Decision decision = decisions.Rank(episode, Find.Merge(copies), known);

        foreach (ReleaseCopy candidate in decision.Ranked)
        {
            ReleaseCopy chosen = await find.FollowAsync(candidate, ct);

            // And the copy that was followed. No shipped listing publishes a
            // magnet, so a torrent's trackers are not known until its own page
            // has been read.
            trackers.AddRange(chosen.Trackers);

            if (chosen.Magnet is null)
            {
                // Reached for and not to be had. Recorded, so the owner can see
                // which site keeps answering with rows nobody can download
                // from, and passed over so the next copy gets its turn.
                decisions.Unreachable(
                    episode,
                    candidate,
                    $"{candidate.Source} named no torrent for it, on the row or on its own page.");

                continue;
            }

            journal.Finished(ActivityStage.Decide, subject, $"chose {chosen.Title}");

            // Under the name a name database published for it, never under
            // the site's own rendering.
            chosen = chosen with { Title = Decisions.NameOf(chosen, known) };

            IReadOnlyList<EpisodeKey> covers = decisions.CoveredBy(episode, ReleaseName.Parse(chosen.Title));

            (EpisodeOutcome outcome, bool stands) = await GrabAsync(episode, chosen, covers, options, ct);

            if (!stands)
            {
                // The client would not have it. A download that never started
                // settles nothing, and the next copy is worth a turn - but the
                // reason is kept, because it is the true answer for this
                // episode if no other copy can be had either.
                refused.Add(outcome.Detail);

                continue;
            }

            decisions.Settle(episode, chosen);

            return outcome with
            {
                Searched = true,
                Considered = AheadOf(chosen, decision.Ranked),
            };
        }

        return null;
    }

    /// <summary>
    /// The copies this one was taken ahead of, with what each was seeded by.
    /// </summary>
    /// <remarks>
    /// Three of them, which is enough to see whether the winner won on a
    /// number or on a whisker, and few enough to read on one line. A count
    /// nobody gave is said as unknown rather than as nought: the difference is
    /// the whole reason the ranking sorts them apart.
    /// </remarks>
    private static string? AheadOf(ReleaseCopy taken, IReadOnlyList<ReleaseCopy> ranked)
    {
        string[] others =
        [
            .. ranked
                .Where(copy => !ReferenceEquals(copy, taken) && copy.Title != taken.Title)
                .Take(3)
                .Select(copy => $"{copy.Title} ({copy.Source}, {Count(copy.Seeders)})"),
        ];

        return others.Length == 0 ? null : $"ahead of {string.Join("; ", others)}";
    }

    private static string Count(int? seeders)
    {
        return seeders is int many ? $"{many} seeders" : "no count given";
    }

    /// <summary>
    /// What to ask the indexers, in the order it is worth asking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A3 said the full release name and nothing else, and A3 is
    /// wrong.</strong> On 22 August 2026 apibay answered
    /// <c>Silo S03E08 1080p WEB H264 CAKES</c> with "No results returned" and
    /// <c>Silo S03E08</c> with twelve rows, the first of them seeded by six
    /// thousand. Four of the eight indexers that cycle read nothing at all off
    /// a release every one of them was carrying, and the episode was reported
    /// as though it did not exist. Both captures are in tests/fixtures.
    /// </para>
    /// <para>
    /// The episode's own number first, because that is the question nearly
    /// every search box can answer. Then the programme on its own, because
    /// EZTV's box is labelled "Search title" and answers a release name with
    /// nothing at all, and because one such answer carries every gap of that
    /// programme this cycle is looking for. Then the release names, which are
    /// exact and worth having wherever a site can use them.
    /// </para>
    /// <para>
    /// What comes back is judged name by name against the profile, which is the
    /// protection A3 was really asking for. 0.3.4 searched broadly and had no
    /// rule saying a row had to be a release of the episode it was asked about,
    /// so whatever came back well seeded was taken. This has that rule, and it
    /// is <see cref="ReleaseFilter.IsFor"/>.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Term, bool Shelf)> Terms(
        TrackedEpisode episode,
        IReadOnlyList<string> names,
        Profile profile)
    {
        // Never more than the owner's own MaxSearchAttempts, counting only the
        // terms that really cost something. The season and the programme are
        // fetched once a cycle however many gaps fall through to them, so
        // charging every gap for them would spend the whole allowance on two
        // requests that were already paid for.
        return Shelves()
            .Select(term => (term, true))
            .Concat(Every().Take(Math.Max(1, profile.MaxSearchAttempts)).Select(term => (term, false)));

        IEnumerable<string> Shelves()
        {
            yield return $"{episode.ShowTitle} S{episode.Key.Season:00}";
            yield return episode.ShowTitle;
        }

        IEnumerable<string> Every()
        {
            yield return $"{episode.ShowTitle} {episode.Key}";

            if (episode.Absolute is int absolute)
            {
                // An absolute-numbered release carries no season tag at all, so
                // the form above finds none of them.
                yield return $"{episode.ShowTitle} {absolute}";
            }

            foreach (string name in names)
            {
                yield return name;
            }
        }
    }

    /// <summary>
    /// Hands one chosen copy over, and says whether the decision stands.
    /// </summary>
    /// <returns>
    /// The outcome, and whether it is the last word on this episode. A client
    /// that would not take the torrent has not decided anything: the next copy
    /// down the ranking is still worth a turn, and nothing this one would have
    /// covered is settled by it.
    /// </returns>
    private async Task<(EpisodeOutcome Outcome, bool Stands)> GrabAsync(
        TrackedEpisode episode,
        ReleaseCopy chosen,
        IReadOnlyList<EpisodeKey> covers,
        CycleOptions options,
        CancellationToken ct)
    {
        string subject = $"{episode.ShowTitle} {episode.Key}";

        if (options.DryRun)
        {
            return (
                new(
                    episode.Key,
                    chosen.Title,
                    chosen.Source,
                    chosen.Seeders,
                    false,
                    "would take it — dry run is on")
                {
                    Magnet = chosen.Magnet,
                    Covers = covers,
                },
                true);
        }

        if (grab is null)
        {
            // Said plainly rather than left looking like a decision nobody
            // made. A plugin with no torrent client decides and hands nothing
            // over, and the page says exactly that.
            return (
                new(
                    episode.Key,
                    chosen.Title,
                    chosen.Source,
                    chosen.Seeders,
                    false,
                    "would take it — there is no torrent client yet")
                {
                    Magnet = chosen.Magnet,
                    Covers = covers,
                },
                true);
        }

        journal.Started(ActivityStage.Grab, subject, chosen.Title);

        // Through the grab, never straight to the client: it is what checks
        // there is room first, and a torrent that fills the disk takes the
        // media server with it — the same disk holds the library and the
        // database.
        Grabbed taken = await grab.TakeAsync(chosen, options.IncompleteFolder, options.DefaultTrackers, ct);

        if (taken.Result != GrabResult.Taken)
        {
            // B2: a client that would not take a magnet is not the episode's
            // fault, so this costs it no search attempt.
            return (
                new(
                    episode.Key,
                    chosen.Title,
                    chosen.Source,
                    chosen.Seeders,
                    false,
                    taken.Reason ?? "the client would not take it and gave no reason"),
                false);
        }

        journal.Finished(ActivityStage.Grab, subject, $"{chosen.Title} from {chosen.Source}");

        return (
            new(
                episode.Key,
                chosen.Title,
                chosen.Source,
                chosen.Seeders,
                true,
                $"taken from {chosen.Source}, {taken.InfoHash}")
            {
                InfoHash = taken.InfoHash,
                Magnet = chosen.Magnet,
                Covers = covers,
            },
            true);
    }
}
