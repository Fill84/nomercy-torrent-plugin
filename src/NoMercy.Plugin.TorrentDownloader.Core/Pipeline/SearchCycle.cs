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
    string Detail);

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
    string IncompleteFolder);

/// <summary>Everything one cycle decided.</summary>
/// <param name="Outcomes">One per episode looked at, in the order they were looked at.</param>
/// <param name="Skipped">Every release refused, with its reason, for the Skipped page.</param>
public sealed record CycleReport(IReadOnlyList<EpisodeOutcome> Outcomes, IReadOnlyList<SkippedRelease> Skipped);

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
    ITorrentEngine? engine = null)
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

        // One episode at a time, because the decisions of each are part of the
        // state of the next: a pack taken for season three settles the rest of
        // season three, and running them together would have two of them grab
        // the same season.
        foreach (TrackedEpisode episode in queue)
        {
            ct.ThrowIfCancellationRequested();

            outcomes.Add(await LookAsync(
                episode,
                byEpisode.GetValueOrDefault(episode.Key, []),
                decisions,
                options,
                ct));
        }

        return new(outcomes, decisions.Skipped);
    }

    private async Task<EpisodeOutcome> LookAsync(
        TrackedEpisode episode,
        IReadOnlyList<string> candidates,
        Decisions decisions,
        CycleOptions options,
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

        try
        {
            string[] acceptable =
            [
                .. candidates.Where(title => decisions.JudgeName(ReleaseName.Parse(title), episode).Accepted),
            ];

            if (acceptable.Length == 0)
            {
                journal.Finished(ActivityStage.Decide, subject, "no acceptable name");

                return new(
                    episode.Key,
                    null,
                    null,
                    null,
                    false,
                    candidates.Count == 0
                        ? "nothing has a name for it yet"
                        : $"none of its {candidates.Count} names is acceptable");
            }

            // In turn, stopping at the first that produces a copy worth taking,
            // and never more than the owner's own MaxSearchAttempts: twenty
            // spellings of one release times seventeen indexers is a cycle that
            // gets the plugin banned from every site it asks.
            foreach (string title in acceptable.Take(Math.Max(1, options.Profile.MaxSearchAttempts)))
            {
                ReleaseName name = ReleaseName.Parse(title);

                IReadOnlyList<ReleaseCopy> copies = await find.SearchAsync(title, ct);

                Decision decision = decisions.Choose(episode, name, copies);

                if (decision.Chosen is null)
                {
                    continue;
                }

                ReleaseCopy chosen = await find.FollowAsync(decision.Chosen, ct);

                journal.Finished(ActivityStage.Decide, subject, $"chose {chosen.Title}");

                return await GrabAsync(episode, chosen, options, ct);
            }

            journal.Finished(ActivityStage.Decide, subject, "nobody is serving an acceptable copy");

            return new(
                episode.Key,
                null,
                null,
                null,
                false,
                $"{acceptable.Length} names, and no copy of any of them is worth taking");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Decide, subject, exception.Message);

            return new(episode.Key, null, null, null, false, exception.Message);
        }
    }

    private async Task<EpisodeOutcome> GrabAsync(
        TrackedEpisode episode,
        ReleaseCopy chosen,
        CycleOptions options,
        CancellationToken ct)
    {
        string subject = $"{episode.ShowTitle} {episode.Key}";

        if (chosen.Magnet is null)
        {
            // Chosen and unreachable: its own page named no torrent either. Not
            // a refusal by the profile, and it must not read like one.
            journal.Failed(ActivityStage.Grab, subject, $"{chosen.Title} offers no way to the torrent.");

            return new(episode.Key, chosen.Title, chosen.Source, chosen.Seeders, false, "no way to the torrent");
        }

        if (options.DryRun)
        {
            return new(
                episode.Key,
                chosen.Title,
                chosen.Source,
                chosen.Seeders,
                false,
                "would take it — dry run is on");
        }

        if (engine is null)
        {
            // Said plainly rather than left looking like a decision nobody
            // made. Sprint 5 writes the client; until it exists this plugin
            // decides and hands nothing over.
            return new(
                episode.Key,
                chosen.Title,
                chosen.Source,
                chosen.Seeders,
                false,
                "would take it — there is no torrent client yet");
        }

        journal.Started(ActivityStage.Grab, subject, chosen.Title);

        TorrentHandle taken = await engine.AddAsync(
            new(chosen.Magnet, chosen.Trackers, options.IncompleteFolder, chosen.SizeBytes),
            ct);

        journal.Finished(ActivityStage.Grab, subject, $"{chosen.Title} from {chosen.Source}");

        return new(
            episode.Key,
            chosen.Title,
            chosen.Source,
            chosen.Seeders,
            true,
            $"taken from {chosen.Source}, {taken.InfoHash}");
    }
}
