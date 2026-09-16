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
    public static async Task WriteAsync(
        CycleReport report,
        IReadOnlyList<TrackedEpisode> looked,
        GrabRepository grabs,
        DateTimeOffset at,
        CancellationToken ct,
        EpisodeRepository? episodes = null)
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
                        outcome.Considered is string ahead
                            ? $"{outcome.Detail} — {ahead}"
                            : outcome.Detail,
                        at,
                        ct);
                }

                continue;
            }

            // Itself at the least: a grab that answers for no episode could
            // never be put back to missing when it failed.
            IReadOnlyList<EpisodeKey> covers = outcome.Covers.Count > 0 ? outcome.Covers : [outcome.Episode];

            await grabs.RecordAsync(
                outcome.Episode,
                titles.GetValueOrDefault(outcome.Episode, string.Empty),
                outcome.Release ?? hash,
                outcome.Source ?? "unknown",
                hash,
                outcome.Magnet,
                covers,
                at,
                ct);
        }

        await CountSearchesAsync(report, at, episodes, ct);
    }

    /// <summary>
    /// Counts a search against every episode one was really made for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nothing counted a search at all. <c>attempts</c> stayed at nought on
    /// every row of the owner's library, and <c>last_search_at</c> stayed null
    /// with it — and that is what the queue is ordered by, so "never searched
    /// first, then longest waiting" ordered every cycle the same way and the
    /// episodes at the end of it were reached last for ever.
    /// </para>
    /// <para>
    /// <strong>B2:</strong> only a search counts. An episode settled by a pack
    /// taken earlier, and one nothing could be asked about, have not been
    /// looked for — and a grab the client refused is not the episode's fault
    /// either, which is why the cycle says whether an indexer was actually
    /// asked rather than leaving this to guess from the outcome.
    /// </para>
    /// <para>
    /// There used to be a give-up here too, once an episode's attempts reached
    /// <c>MaxSearchAttempts</c>. It never held — the refresh at the top of the
    /// next run derived the episode as missing again and kept the count
    /// climbing regardless — so the owner dropped the limit outright rather
    /// than have it hold for a time. An attempt is still worth recording; there
    /// is nothing left it can exhaust.
    /// </para>
    /// </remarks>
    private static async Task CountSearchesAsync(
        CycleReport report,
        DateTimeOffset at,
        EpisodeRepository? episodes,
        CancellationToken ct)
    {
        if (episodes is null)
        {
            return;
        }

        foreach (EpisodeOutcome outcome in report.Outcomes)
        {
            if (!outcome.Searched)
            {
                continue;
            }

            await episodes.RecordSearchAsync(outcome.Episode, at, ct);
        }
    }
}
