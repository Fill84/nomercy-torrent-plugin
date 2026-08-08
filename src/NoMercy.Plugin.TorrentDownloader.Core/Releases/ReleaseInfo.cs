// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

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

    /// <summary>
    /// Every tracker any source named for this info hash, merged by the aggregator.
    /// One release listed on four sites announces four different tracker sets, and a
    /// bigger swarm is a faster download.
    /// </summary>
    public IReadOnlyList<string> Trackers { get; init; } = [];
}
