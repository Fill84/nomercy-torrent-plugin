namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;

public record RssItem
{
    public required string Title { get; init; }
    public string? Link { get; init; }
    public string? Guid { get; init; }
    public DateTimeOffset? Published { get; init; }
    public IReadOnlyList<string> Categories { get; init; } = [];
    public string? EnclosureUrl { get; init; }
    public long EnclosureLength { get; init; }
    public string? EnclosureType { get; init; }
}
