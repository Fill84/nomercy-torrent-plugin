using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Checks, once, whether each delivery recorded before the plugin wrote down where its release came from is a
/// name the name sources give.
/// </summary>
/// <param name="grabs">Where the answer is written down.</param>
/// <param name="library">The library, for which deliveries are still what it holds.</param>
/// <param name="applied">Which shows are switched on.</param>
/// <param name="logger">The server's log.</param>
public sealed class DeliveredNames(GrabRepository grabs, ILibrary library, IAppliedSettings applied, ILogger logger)
{
    /// <summary>How many deliveries one run asks about.</summary>
    /// <remarks>
    /// Each is a paced question to every name source — fifteen seconds apart on most of them — and 0.8.0
    /// delivered hundreds, so asked all at once the first run after the update spent an hour on the past
    /// before it looked for anything new. Ten is a couple of minutes a run; the rest are asked on the runs
    /// after (<c>PROGRESS.md</c> § Decisions, 16 October 2026).
    /// </remarks>
    public const int PerRun = 10;

    /// <summary>Checks every delivery not yet checked; answers how many it found no name source names.</summary>
    /// <remarks>
    /// <para>
    /// The owner's rule of 15 October 2026. Asked of the same search a run asks
    /// (<see cref="DeliveredCheck"/>), and the answer written down, so a delivery is checked once and not on
    /// every run.
    /// </para>
    /// <para>
    /// A delivery a source could not answer about is left unchecked and asked about again on the next run: a
    /// site that did not answer has proved nothing against a release.
    /// </para>
    /// </remarks>
    public async Task<int> CheckAsync(IReleaseNames names, CancellationToken ct)
    {
        MissingRefresh refresh = new(library, applied, TimeProvider.System, await grabs.EveryAsync(ct));
        DeliveredCheck check = new(names);

        int unnamed = 0;
        int asked = 0;

        foreach (UncheckedDelivery one in await refresh.UncheckedAsync(ct))
        {
            if (one.Covered.Count == 0)
            {
                continue;
            }

            // Counted whether or not a source answers: the cost is the asking.
            if (asked++ == PerRun)
            {
                break;
            }

            if (await check.NamedAsync(one.Grab.ReleaseTitle, one.Covered, ct) is not bool named)
            {
                logger.LogDebug(
                    "Whether {Release} is a release the name sources name could not be said, because a name source did not answer; it is asked again on the next run.",
                    one.Grab.ReleaseTitle);

                continue;
            }

            await grabs.NamedAsync(one.Grab.InfoHash, named, ct);

            if (named)
            {
                continue;
            }

            unnamed++;

            logger.LogInformation(
                "{Release} is a release no name source names, so {Episodes} is replaced once a release the name sources name has downloaded.",
                one.Grab.ReleaseTitle,
                string.Join(", ", one.Covered.Select(episode => $"{episode.ShowTitle} {episode.Key}")));
        }

        return unnamed;
    }
}
