namespace NoMercy.Plugin.TorrentDownloader.Core.Releases;

public record ReleaseInfo
{
    public required string IndexerName { get; init; }
    public required string TorrentId { get; init; }
    public required string Title { get; init; }
    public string? DetailUrl { get; init; }
    public string? MagnetUri { get; init; }
    public string? DownloadUrl { get; init; }
    public string? InfoHash { get; init; }
    public long SizeBytes { get; init; }
    public int Seeders { get; init; }
    public int Leechers { get; init; }
    public int IndexerPriority { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
}
