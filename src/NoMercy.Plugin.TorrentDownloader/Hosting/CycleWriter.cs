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
public sealed class CycleWriter(
    IReadOnlyList<TrackedEpisode> looked,
    GrabRepository grabs,
    EpisodeRepository episodes,
    int maxAttempts,
    Func<DateTimeOffset> now) : ICycleJournal
{
    private readonly Dictionary<EpisodeKey, TrackedEpisode> _looked =
        looked.ToDictionary(episode => episode.Key);

    public async Task DecidedAsync(
        EpisodeOutcome outcome,
        IReadOnlyList<SkippedRelease> refused,
        CancellationToken ct)
    {
        DateTimeOffset at = now();

        await CycleRecord.WriteAsync(
            new([outcome], refused),
            _looked.TryGetValue(outcome.Episode, out TrackedEpisode? episode) ? [episode] : [],
            grabs,
            at,
            ct,
            episodes,
            maxAttempts);
    }
}
