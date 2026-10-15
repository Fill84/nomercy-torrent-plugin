using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Writes one episode's decision down the moment the cycle makes it.
/// </summary>
/// <remarks>
/// <para>
/// The same writing <see cref="CycleRecord"/> does, one episode at a time
/// instead of all of them at the end. Over twenty-eight gaps the end is half an
/// hour away: until it arrived the Downloads, Skipped and History pages said
/// nothing at all, and a run stopped, cancelled or crashed in the meantime
/// threw away every decision it had made.
/// </para>
/// <para>
/// It is handed the show titles up front because an outcome carries a key and
/// not a name, and the episode it is about may have stopped being missing by
/// the time anything reads it.
/// </para>
/// </remarks>
/// <param name="looked">The episodes the run looks at, for their show titles.</param>
/// <param name="grabs">Where a decision is written.</param>
/// <param name="episodes">Where a search attempt is counted.</param>
/// <param name="now">When a decision is made.</param>
/// <param name="handedOver">
/// A transfers pass, started once a torrent the client was handed has its grab written down. The client
/// takes a torrent before its row exists, and a pass in that gap stops it as nobody's and keeps its files;
/// with no pass after the row, it waited for the next run, a whole cadence later, to be handed back.
/// </param>
public sealed class CycleWriter(
    IReadOnlyList<TrackedEpisode> looked,
    GrabRepository grabs,
    EpisodeRepository episodes,
    Func<DateTimeOffset> now,
    Func<CancellationToken, Task>? handedOver = null) : ICycleJournal
{
    private readonly Dictionary<EpisodeKey, TrackedEpisode> _looked =
        looked.ToDictionary(episode => episode.Key);

    public async Task DecidedAsync(EpisodeOutcome outcome, CancellationToken ct)
    {
        DateTimeOffset at = now();

        await CycleRecord.WriteAsync(
            new([outcome]),
            _looked.TryGetValue(outcome.Episode, out TrackedEpisode? episode) ? [episode] : [],
            grabs,
            at,
            ct,
            episodes);

        // Now the row and the client can agree within this run, as a torrent added by hand does.
        if (outcome.HandedOver && outcome.InfoHash is not null && handedOver is not null)
        {
            await handedOver(ct);
        }
    }
}
