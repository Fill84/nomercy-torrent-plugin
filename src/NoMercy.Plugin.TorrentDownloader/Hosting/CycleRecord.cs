using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Writing down what one cycle decided.
/// </summary>
/// <remarks>
/// A cycle answered with a report and nothing ever wrote it anywhere: the
/// Downloads page was empty while a torrent was running, the Skipped page was
/// empty however much had been refused, and a restart lost every decision the
/// cycle had made. A decision the client has been handed is a fact about an
/// episode, and one it has not been handed is not.
/// </remarks>
public static class CycleRecord
{
    /// <summary>Records every grab and every refusal of one cycle.</summary>
    /// <param name="report">What the cycle decided.</param>
    /// <param name="looked">The episodes it looked at, for the show titles.</param>
    /// <param name="grabs">Where it is written.</param>
    /// <param name="at">When the cycle finished.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="episodes">Where a search attempt is counted, when there is one to count it in.</param>
    /// <param name="maxAttempts">How many searches an episode gets before it is given up on for now.</param>
    public static async Task WriteAsync(
        CycleReport report,
        IReadOnlyList<TrackedEpisode> looked,
        GrabRepository grabs,
        DateTimeOffset at,
        CancellationToken ct,
        EpisodeRepository? episodes = null,
        int maxAttempts = 0)
    {
        Dictionary<EpisodeKey, string> titles = [];

        foreach (TrackedEpisode episode in looked)
        {
            titles[episode.Key] = episode.ShowTitle;
        }

        foreach (EpisodeOutcome outcome in report.Outcomes)
        {
            // Handed over and known by a hash. A decision nothing was handed is
            // not a fact about an episode, and a row for it would have the
            // Downloads page show a torrent nothing is downloading.
            if (!outcome.HandedOver || outcome.InfoHash is not string hash)
            {
                // Decided and not handed over: dry run, no client yet, or a
                // client that would not take it. Written down, because
                // otherwise a cycle that found the right release for every
                // episode leaves a Skipped page full of refusals and no trace
                // of one thing it would have taken - and that page is the only
                // evidence the owner has.
                if (outcome.Release is string decided)
                {
                    await grabs.RecordDecidedAsync(
                        outcome.Episode,
                        titles.GetValueOrDefault(outcome.Episode, string.Empty),
                        decided,
                        outcome.Source,
                        outcome.Detail,
                        at,
                        ct);
                }

                continue;
            }

            await grabs.RecordAsync(
                outcome.Episode,
                titles.GetValueOrDefault(outcome.Episode, string.Empty),
                outcome.Release ?? hash,
                outcome.Source ?? "unknown",
                hash,
                outcome.Magnet,

                // Itself at the least: a grab that answers for no episode could
                // never be put back to missing when it failed.
                outcome.Covers.Count > 0 ? outcome.Covers : [outcome.Episode],
                at,
                ct);
        }

        await CountSearchesAsync(report, looked, at, episodes, maxAttempts, ct);

        foreach (SkippedRelease skipped in report.Skipped)
        {
            await grabs.RecordSkippedAsync(
                skipped.Episode,
                titles.GetValueOrDefault(skipped.Episode, string.Empty),
                skipped.Title,
                skipped.Source,
                skipped.Reason,
                at,
                ct);
        }
    }

    /// <summary>
    /// Counts a search against every episode one was really made for, and gives
    /// up on the ones that have had their share.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing counted a search at all. <c>attempts</c> stayed at nought on
    /// every row of the owner's library, so <c>MaxSearchAttempts</c> decided
    /// nothing, no episode ever reached <em>given up for now</em>, and the
    /// Queue page's third list could not fill. <c>last_search_at</c> stayed
    /// null with it — and that is what the queue is ordered by, so "never
    /// searched first, then longest waiting" ordered every cycle the same way
    /// and the episodes at the end of it were reached last for ever.
    /// </para>
    /// <para>
    /// <strong>B2:</strong> only a search counts. An episode settled by a pack
    /// taken earlier, and one nothing could be asked about, have not been
    /// looked for — and a grab the client refused is not the episode's fault
    /// either, which is why the cycle says whether an indexer was actually
    /// asked rather than leaving this to guess from the outcome.
    /// </para>
    /// </remarks>
    private static async Task CountSearchesAsync(
        CycleReport report,
        IReadOnlyList<TrackedEpisode> looked,
        DateTimeOffset at,
        EpisodeRepository? episodes,
        int maxAttempts,
        CancellationToken ct)
    {
        if (episodes is null)
        {
            return;
        }

        Dictionary<EpisodeKey, int> already = [];

        foreach (TrackedEpisode episode in looked)
        {
            already[episode.Key] = episode.Attempts;
        }

        foreach (EpisodeOutcome outcome in report.Outcomes)
        {
            if (!outcome.Searched)
            {
                continue;
            }

            await episodes.RecordSearchAsync(outcome.Episode, at, ct);

            if (outcome.HandedOver || maxAttempts <= 0)
            {
                continue;
            }

            // The attempt just recorded included. Giving up is a consequence of
            // the attempts already made, so the count that decides it is the
            // one after this search rather than the one before.
            if (already.GetValueOrDefault(outcome.Episode) + 1 >= maxAttempts)
            {
                await episodes.MarkUnavailableAsync(outcome.Episode, ct);
            }
        }
    }
}
