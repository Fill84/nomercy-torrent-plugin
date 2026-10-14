using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>What a search says in the log once it is over.</summary>
/// <remarks>
/// <para>
/// GitHub issue #7: Information is one line per step the owner sees, and search and grab are two of them.
/// A search said that it had started and nothing more — what it grabbed was on the Activity page and in no
/// log, so a log pasted into an issue could not say whether anything had been found at all.
/// </para>
/// <para>
/// Why an episode was not grabbed is the decision behind the step, so it is Debug: on a library with two
/// hundred gaps it is two hundred lines a run, which is the file's business and not the server's.
/// </para>
/// </remarks>
public static class SearchSummary
{
    public static void Tell(ILogger log, CycleReport report)
    {
        int grabbed = report.Outcomes.Count(one => one.HandedOver);

        log.LogInformation(
            "The search looked at {Count} episodes and grabbed {Grabbed}.",
            report.Outcomes.Count,
            grabbed);

        foreach (EpisodeOutcome one in report.Outcomes)
        {
            if (one.HandedOver)
            {
                log.LogInformation(
                    "{Episode} of show {Show}: grabbed {Release} from {Source}, {Seeders} seeders.",
                    one.Episode,
                    one.Episode.ShowId,
                    one.Release,
                    one.Source,
                    one.Seeders?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "an unknown number of");
            }
            else
            {
                log.LogDebug(
                    "{Episode} of show {Show}: not grabbed. {Detail}",
                    one.Episode,
                    one.Episode.ShowId,
                    one.Detail);
            }
        }
    }
}
