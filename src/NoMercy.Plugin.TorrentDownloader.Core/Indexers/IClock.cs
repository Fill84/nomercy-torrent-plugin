namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public interface IClock
{
    DateTimeOffset UtcNow { get; }

    Task DelayAsync(TimeSpan duration, CancellationToken ct);
}
