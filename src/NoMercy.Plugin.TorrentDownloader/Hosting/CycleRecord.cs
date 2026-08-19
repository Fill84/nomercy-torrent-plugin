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
    public static async Task WriteAsync(
        CycleReport report,
        IReadOnlyList<TrackedEpisode> looked,
        GrabRepository grabs,
        DateTimeOffset at,
        CancellationToken ct)
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
}
