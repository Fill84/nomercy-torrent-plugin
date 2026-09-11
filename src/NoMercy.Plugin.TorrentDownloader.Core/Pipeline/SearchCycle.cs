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

    /// <summary>The hosts of the owner's own private trackers.</summary>
    /// <remarks>
    /// Never announced to for somebody else's torrent. A private tracker's
    /// announce address carries the owner's passkey, and this list travels with
    /// every grab — so one learned off a public page would hand their
    /// credentials to every public swarm they download from. It became a real
    /// risk the moment a grab started carrying the trackers of every indexer
    /// that had the torrent, which before then was none at all.
    /// </remarks>
    public IReadOnlyCollection<string> OwnTrackerHosts { get; init; } = [];
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

        journal.Counted(RunCounter.Episodes, queue.Length);

        Decisions decisions = new(options.Profile, queue, options.Blacklisted);

        // What the pool already knows, read once. The sources are asked inside
        // the loop, one episode at a time, so that an episode is handed to the
        // client the moment it is decided rather than after every other
        // episode's name has been asked for.
        PooledNames pooled = await names.FromPoolAsync(queue, ct);

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
        AskedThisCycle asked = new();

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

            // An episode a pack taken earlier this cycle already answers for is
            // not asked about at all: a question to a source is a paced request,
            // and this one has no use.
            IReadOnlyList<string> candidates = decisions.Settled(episode.Key)
                ? []
                : await names.NamesForAsync(episode, pooled, options.Profile, ct);

            EpisodeOutcome outcome = await LookAsync(
                episode,
                candidates,
                decisions,
                options,
                trackers,
                answered,
                asked,
                ct);

            outcomes.Add(outcome);

            journal.Counted(RunCounter.Decided);

            if (outcome.HandedOver)
            {
                journal.Counted(RunCounter.Taken);
            }

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
        AskedThisCycle asked,
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

            // **Stage 3 of docs/03-architecture.md, which was never built.** The
            // profile applied to NAMES, before an indexer is touched: slot,
            // quality, codec, language, group. It was only ever applied to the
            // copies that came back, so a name the owner could never accept was
            // asked of every indexer, waited out every host's pace, and had
            // every row it returned thrown away.
            //
            // The owner watched four indexers being asked for
            // South.Park.S15E12.1.Prozent.German.DL.AC3D.1080p.BluRay.x264-JaJunge
            // on 2 September 2026 with English only on. It is a real scene
            // release and a real PreDB name — their pool holds 2,238 names in
            // other languages, and every one of them cost a request at every
            // indexer that carries the show.
            IReadOnlyList<string> wanted = Wanted(candidates, episode, decisions, refused, subject);

            // The whole ladder in one pass: each of the source's names letter
            // for letter and then without its punctuation, then what this plugin
            // makes up, and each indexer asked down it until that indexer
            // answers. Nothing is judged until every site has finished — the
            // owner's rule, and the reason the judging is one call below rather
            // than one per rung.
            IReadOnlyList<SearchTerm> ladder = SearchTerm.Ladder(
                wanted,
                Rungs(episode, options.Profile).Select(rung => rung.Term));

            await AskAsync(ladder, episode, gathered, answered, asked, trackers, ct);

            searched = true;

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
            ReleaseCopy chosen = await find.ResolveAsync(candidate, ct);

            // And the copy that was resolved. No shipped listing publishes a
            // magnet, so a torrent's trackers are not known until the row's own
            // page has been read — on every indexer that has it, which is where
            // this list comes from.
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
    /// Asks every indexer for each term, once per cycle per term.
    /// </summary>
    /// <remarks>
    /// A shelf's answer is shared with every gap it could cover, because a
    /// season or a programme was asked for the whole of itself. An episode's
    /// own search was asked about that episode, and letting its leftovers
    /// settle another gap is how Sugar S02E08 came to be decided by a stray row
    /// while the release everybody was seeding went unfetched.
    /// </remarks>
    private async Task AskAsync(
        IReadOnlyList<SearchTerm> ladder,
        TrackedEpisode episode,
        List<ReleaseCopy> gathered,
        List<ReleaseCopy> answered,
        AskedThisCycle asked,
        List<string> trackers,
        CancellationToken ct)
    {
        IReadOnlyList<ReleaseCopy> copies = await find.SearchAsync(
            ladder,
            episode.Kind,
            ct,
            asked,
            about: $"{episode.ShowTitle} {episode.Key}");

        // Every copy, taken or not: a tracker on a release the profile refused
        // is serving the same swarm as the one it accepted.
        trackers.AddRange(copies.SelectMany(copy => copy.Trackers));

        // A season's rungs are at the foot of every gap's ladder, so what one
        // gap's search turned up is a candidate for the others. A candidate and
        // never an answer: each gap is still decided on its own, which is what
        // let a stray row settle Sugar S02E08 while the release everybody was
        // seeding went unfetched.
        answered.AddRange(copies);

        // Once. It was added twice, so every copy this episode's own search
        // brought back went into its decision two times over.
        gathered.AddRange(copies);
    }

    /// <summary>
    /// Which of the source's names are worth asking an indexer for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The profile applied to names, which is stage 3 of
    /// docs/03-architecture.md.</strong> It is the same rule that judges a copy
    /// — <see cref="ReleaseFilter.JudgeName"/> — and it needs no network to say
    /// that a German release is not wanted where English only is on, that a
    /// 720p one is not 1080p, or that h265 is not h264.
    /// </para>
    /// <para>
    /// A name refused here costs nothing. Asked for, it costs one request at
    /// every indexer that carries the show, waits out each of their paces, and
    /// has every row it answers with thrown away by the same rule one step
    /// later.
    /// </para>
    /// <para>
    /// The reasons are kept, not thrown away: an episode where every name was
    /// refused must be able to say so, or it reads as a search that found
    /// nothing.
    /// </para>
    /// </remarks>
    private IReadOnlyList<string> Wanted(
        IReadOnlyList<string> candidates,
        TrackedEpisode episode,
        Decisions decisions,
        List<string> refused,
        string subject)
    {
        List<string> worth = [];

        foreach (string candidate in candidates)
        {
            // The very judgement the ranking makes one step later, and it needs
            // no network to make it.
            Verdict verdict = decisions.JudgeName(ReleaseName.Parse(candidate), episode);

            if (verdict.Accepted)
            {
                worth.Add(candidate);
                journal.Noted(ActivityStage.Decide, subject, $"asking the indexers for {candidate}");

                continue;
            }

            refused.Add(verdict.Reason);

            // On the page, name and reason: the owner could not see why a
            // source's name never reached an indexer, and on 11 September 2026
            // the only name there was for Dark Matter S02E03 was a MULTi release
            // English only refuses.
            journal.Counted(RunCounter.NamesRefused);
            journal.Noted(ActivityStage.Decide, subject, $"refused {candidate}: {verdict.Reason}");
        }

        return worth;
    }

    /// <summary>
    /// The questions this plugin makes up when the sources cannot answer, each
    /// carrying the quality the owner asked for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner's resolution is part of the question.</strong> Their
    /// decision of 10 September 2026, after watching this ask TorrentGalaxy for
    /// <c>South Park S15</c> and drag back a hundred rows of every season and
    /// every quality to throw nearly all of it away. Measured against all nine
    /// indexers that day, none of them refusing either shape:
    /// <c>South Park S15E12</c> answered 122 torrents and
    /// <c>South Park S15E12 1080p</c> answered 38, the wanted release top of
    /// both. Two thirds less to carry, read and refuse.
    /// </para>
    /// <para>
    /// <strong>The codec is not in the question and cannot be.</strong> The same
    /// encoder is published as <c>H.264</c>, <c>H264</c>, <c>x264</c> and
    /// <c>X 264</c> depending on the group, so naming one of them hides the
    /// other three. It is judged on the rows, where every spelling is
    /// understood.
    /// </para>
    /// <para>
    /// <strong>The programme on its own is gone.</strong> <c>Silo</c> as a
    /// question answers with the whole programme — every season, every episode,
    /// every quality — and every row of it had to be fetched, read and refused.
    /// It was the broadest question this plugin asked and the one the owner saw
    /// bringing back rubbish.
    /// </para>
    /// <para>
    /// The season shelf stays, because it is how a season pack is found at all,
    /// and it is asked once a cycle however many gaps share it.
    /// </para>
    /// </remarks>
    private static IEnumerable<(string Term, bool Shelf)> Rungs(TrackedEpisode episode, Profile profile)
    {
        // Nothing appended when the owner has set no ceiling: an empty quality
        // would put a trailing space into every query and narrow nothing.
        string quality = profile.MaximumResolution.Trim();
        string wanted = quality.Length > 0 ? $" {quality}" : string.Empty;

        // The episode with the owner's quality, then without it. Narrowest
        // first: a question that names the quality brings back a third of what
        // the same question without it does, measured against all nine indexers
        // on 10 September 2026 — 38 torrents against 122.
        if (wanted.Length > 0)
        {
            yield return ($"{episode.ShowTitle} {episode.Key}{wanted}", false);
        }

        yield return ($"{episode.ShowTitle} {episode.Key}", false);

        if (episode.Absolute is int absolute)
        {
            // An absolute-numbered release carries no season tag at all, so the
            // forms above find none of them.
            if (wanted.Length > 0)
            {
                yield return ($"{episode.ShowTitle} {absolute}{wanted}", false);
            }

            yield return ($"{episode.ShowTitle} {absolute}", false);
        }

        // And only then the season, which is the one question a season pack can
        // be found by at all. A shelf: it is asked once a cycle however many
        // gaps of that season fall through to it, and every one of them is
        // entitled to the answer.
        if (wanted.Length > 0)
        {
            yield return ($"{episode.ShowTitle} S{episode.Key.Season:00}{wanted}", true);
        }

        yield return ($"{episode.ShowTitle} S{episode.Key.Season:00}", true);
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
        Grabbed taken = await grab.TakeAsync(
            chosen,
            options.IncompleteFolder,
            options.DefaultTrackers,
            options.OwnTrackerHosts,
            ct);

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
