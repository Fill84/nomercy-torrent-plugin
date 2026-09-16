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
    /// looked for — counting either would credit an indexer asking that never
    /// happened to an episode that was never searched for once.
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
/// <param name="Settings">What applies to each show of the run, with its library's counted in.</param>
/// <param name="Blacklisted">Keys already refused, read once for the cycle.</param>
/// <param name="DryRun">
/// Decide everything and hand nothing over. It says what to do with a decision
/// rather than what makes one acceptable, which is why it is not a show's
/// setting.
/// </param>
/// <param name="IncompleteFolder">Where a download lands while it runs.</param>
public sealed record CycleOptions(
    SettingsByShow Settings,
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
public sealed record CycleReport(IReadOnlyList<EpisodeOutcome> Outcomes)
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
/// The whole chain for one run: names, the indexer round, the winner, the grab.
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
    IReleaseNames names,
    Find find,
    IActivityJournal journal,
    Grab? grab = null,
    ICycleJournal? written = null)
{
    private readonly IndexerRound _round = new(find, journal);

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

        Decisions decisions = new(options.Settings, options.Blacklisted);

        // Every name source's feed, read at once before any episode is worked
        // on (docs/specs/run.md). An episode no feed named is looked up inside
        // the loop, one episode at a time, so that it is handed to the client
        // the moment it is decided rather than after every other episode's name
        // has been asked for.
        FeedNamesTaken taken = await names.ReadFeedsAsync(queue, ct);

        List<EpisodeOutcome> outcomes = [];

        // Every tracker anything published this cycle. Kept in the order they
        // were met so the owner's settings do not churn.
        List<string> trackers = [];

        // What each indexer has already been asked this run (run.md: a
        // question already asked of an indexer during a run is not asked of it
        // again). Thrown away when the run ends, so the next run asks again
        // from nothing — which is what an episode with no torrent yet needs.
        AskedThisCycle asked = new();

        // One episode at a time, because the decisions of each are part of the
        // state of the next: a pack taken for season three settles the rest of
        // season three, and running them together would have two of them grab
        // the same season.
        foreach (TrackedEpisode episode in queue)
        {
            ct.ThrowIfCancellationRequested();

            // An episode a pack taken earlier this cycle already answers for is
            // not asked about at all: a question to a source is a paced request,
            // and this one has no use.
            IReadOnlyList<string> candidates = decisions.Settled(episode.Key)
                ? []
                : [.. (await names.NamesForAsync(episode, taken, ct)).Select(name => name.Title).Distinct(StringComparer.Ordinal)];

            EpisodeOutcome outcome = await LookAsync(
                episode,
                candidates,
                decisions,
                options,
                trackers,
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
                await written.DecidedAsync(outcome, ct);
            }
        }

        return new(outcomes) { Trackers = trackers };
    }

    private async Task<EpisodeOutcome> LookAsync(
        TrackedEpisode episode,
        IReadOnlyList<string> candidates,
        Decisions decisions,
        CycleOptions options,
        List<string> trackers,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        string subject = $"{episode.ShowTitle} {episode.Key}";

        if (decisions.Settled(episode.Key))
        {
            // Already answered for by something taken earlier in this cycle.
            return new(episode.Key, null, null, null, false, "settled by a grab taken earlier this cycle");
        }

        journal.Started(ActivityStage.Decide, subject, $"{candidates.Count} names");

        // Why nothing was taken, in the words the owner can act on: every name
        // refused and why, a client that would not take a torrent.
        List<string> refused = [];

        try
        {
            IReadOnlyList<IReadOnlyList<string>> groups = Wanted(candidates, episode, decisions, refused, subject);

            foreach (IReadOnlyList<string> group in groups)
            {
                IReadOnlyList<RankedTorrent> ranked = await _round.AskAsync(group, episode, options.Blacklisted, asked, ct);

                // Every tracker of every torrent the round found, taken or not:
                // TrackerBook decides what is kept.
                trackers.AddRange(ranked.SelectMany(torrent => torrent.Torrent.Trackers));

                if (await TakeAsync(episode, ranked, decisions, options, subject, trackers, refused, ct) is EpisodeOutcome taken)
                {
                    return taken;
                }

                // indexer-search.md: an empty group hands over to the group with
                // one wish fewer.
                journal.Noted(ActivityStage.Decide, subject, $"no torrent for {string.Join(", ", group)}");
            }

            // indexer-search.md § When no release name gives a torrent: the show and
            // episode, and the best result that names it and meets the show's settings.
            if (await ByEpisodeAsync(episode, decisions, options, subject, trackers, refused, asked, ct) is EpisodeOutcome found)
            {
                return found;
            }

            string none = refused.Count > 0
                ? refused[^1]
                : groups.Count == 0
                    ? "no name source gave a release name for it, and no indexer listed a torrent for the episode"
                    : "no indexer listed a torrent for any of its release names, or for the episode";

            journal.Finished(ActivityStage.Decide, subject, none);

            return new(episode.Key, null, null, null, false, none) { Searched = true };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Decide, subject, exception.Message);

            return new(episode.Key, null, null, null, false, exception.Message);
        }
    }

    /// <summary>
    /// Asks every indexer for the show and episode, and takes the best result that names it and meets its
    /// show's settings.
    /// </summary>
    /// <remarks>
    /// <c>docs/specs/indexer-search.md</c> § When no release name gives a torrent, the owner's rule of
    /// 16 September 2026. The results that count go through the same merge and the same winner as a release
    /// name's, one wish group at a time, the group carrying the most wishes first.
    /// </remarks>
    private async Task<EpisodeOutcome?> ByEpisodeAsync(
        TrackedEpisode episode,
        Decisions decisions,
        CycleOptions options,
        string subject,
        List<string> trackers,
        List<string> refused,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        journal.Noted(ActivityStage.Decide, subject, $"no release name gave a torrent, so asking the indexers for {subject}");

        IReadOnlyList<RankedTorrent> ranked = await _round.AskForEpisodeAsync(
            episode,
            title => decisions.JudgeName(ReleaseName.Parse(title), episode).Accepted,
            options.Blacklisted,
            asked,
            ct);

        trackers.AddRange(ranked.SelectMany(torrent => torrent.Torrent.Trackers));

        foreach (IReadOnlyList<string> group in WishGroups.Of(ranked.Select(torrent => torrent.Torrent.Title), decisions.SettingsFor(episode).Wishes))
        {
            HashSet<string> titles = new(group, StringComparer.Ordinal);
            RankedTorrent[] inGroup = [.. ranked.Where(torrent => titles.Contains(torrent.Torrent.Title))];

            if (await TakeAsync(episode, inGroup, decisions, options, subject, trackers, refused, ct) is EpisodeOutcome taken)
            {
                return taken;
            }
        }

        return null;
    }

    /// <summary>
    /// Offers the winner to the client, and the next torrent in winning order when it cannot be offered.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/specs/indexer-search.md</c> § Handing the winner over: only the winning torrent is offered, and
    /// no other hash of the same release is started. The race of two hashes is gone.
    /// </para>
    /// <para>
    /// The next one is tried only when the winner cannot be had: its torrent named nowhere, or a client that
    /// would not take it — the disk is full, say. The reason is kept, because it is the true answer for this
    /// episode if nothing else can be had either.
    /// </para>
    /// </remarks>
    private async Task<EpisodeOutcome?> TakeAsync(
        TrackedEpisode episode,
        IReadOnlyList<RankedTorrent> ranked,
        Decisions decisions,
        CycleOptions options,
        string subject,
        List<string> trackers,
        List<string> refused,
        CancellationToken ct)
    {
        foreach (RankedTorrent candidate in ranked)
        {
            // Every indexer that listed it, asked once for the artefact its
            // trackers are on; a page already read in the round is not read again.
            ReleaseCopy chosen = await find.ResolveAsync(candidate.Torrent, ct);

            trackers.AddRange(chosen.Trackers);

            if (chosen.Magnet is null)
            {
                refused.Add($"{candidate.Torrent.Title} named no torrent on any indexer that listed it.");

                continue;
            }

            journal.Finished(ActivityStage.Decide, subject, $"chose {chosen.Title}, on {candidate.Indexers.Count} indexers");

            (EpisodeOutcome outcome, bool stands) = await GrabAsync(episode, chosen, [episode.Key], options, ct);

            if (!stands)
            {
                refused.Add(outcome.Detail);

                continue;
            }

            decisions.Settle(episode);

            return outcome with
            {
                Searched = true,
                Considered = AheadOf(candidate, ranked),
            };
        }

        return null;
    }

    /// <summary>The torrents this one won ahead of, with how many indexers listed each.</summary>
    /// <remarks>Three of them, enough to see whether the winner won by a margin, and few enough to read on one line.</remarks>
    private static string? AheadOf(RankedTorrent taken, IReadOnlyList<RankedTorrent> ranked)
    {
        string[] others =
        [
            .. ranked
                .Where(one => !ReferenceEquals(one, taken))
                .Take(3)
                .Select(one => $"{one.Torrent.InfoHash} ({Listed(one.Indexers.Count)})"),
        ];

        return others.Length == 0 ? null : $"on {Listed(taken.Indexers.Count)}, ahead of {string.Join("; ", others)}";
    }

    private static string Listed(int indexers)
    {
        return indexers == 1 ? "1 indexer" : $"{indexers} indexers";
    }

    /// <summary>
    /// Which of the source's names are worth asking an indexer for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The show's settings applied to names</strong>
    /// (<c>docs/specs/release-names.md</c>), through <see cref="NameJudge.JudgeName"/>.
    /// It needs no network to say that a 720p release is not 1080p, that h265 is
    /// not h264, or that a name carries a forbidden tag.
    /// </para>
    /// <para>
    /// A name refused here costs nothing. Asked for, it costs one request at
    /// every indexer that carries the show and waits out each of their paces —
    /// and an indexer result is not judged against the settings again, so a name
    /// that should have been refused would be downloaded.
    /// </para>
    /// <para>
    /// The reasons are kept, not thrown away: an episode where every name was
    /// refused must be able to say so, or it reads as a search that found
    /// nothing.
    /// </para>
    /// </remarks>
    private IReadOnlyList<IReadOnlyList<string>> Wanted(
        IReadOnlyList<string> candidates,
        TrackedEpisode episode,
        Decisions decisions,
        List<string> refused,
        string subject)
    {
        List<string> worth = [];

        foreach (string candidate in candidates)
        {
            // Judged once, here: an indexer's rows are not judged again.
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

        // Grouped by the show's wishes, most first (release-names.md): each group
        // is one round of the indexers, and a group that finds no torrent hands
        // over to the group with one wish fewer.
        return WishGroups.Of(worth, decisions.SettingsFor(episode).Wishes);
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
