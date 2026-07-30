// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public sealed partial class RssIndexer(
    string name,
    int priority,
    Uri feedUrl,
    HttpClient http,
    IReadOnlyList<string>? categories = null
) : IIndexer
{
    [GeneratedRegex(
        @"btih:([0-9a-f]{40}|[0-9a-z]{32})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant
    )]
    private static partial Regex InfoHashPattern();

    public string Name => name;

    public int Priority => priority;

    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        string body = await IndexerHttp.GetStringAsync(http, feedUrl, name, "feed", ct);

        return RssFeedParser
            .Parse(body)
            .Where(InConfiguredCategories)
            .Select(ToRelease)
            .ToArray();
    }

    private bool InConfiguredCategories(RssItem item) =>
        categories is null
        || categories.Count == 0
        || item.Categories.Any(category => categories.Contains(category, StringComparer.OrdinalIgnoreCase));

    private ReleaseInfo ToRelease(RssItem item)
    {
        bool isMagnet = item.Link?.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) == true;
        string? magnet = isMagnet ? item.Link : null;
        Match hash = InfoHashPattern().Match(magnet ?? string.Empty);

        return new ReleaseInfo
        {
            IndexerName = name,
            TorrentId = item.Guid ?? item.Link ?? item.Title,
            Title = item.Title,
            DetailUrl = isMagnet ? null : item.Link,
            MagnetUri = magnet,
            DownloadUrl = item.EnclosureUrl,
            InfoHash = hash.Success ? hash.Groups[1].Value.ToLowerInvariant() : null,
            SizeBytes = item.EnclosureLength,
            IndexerPriority = priority,
            PublishedAt = item.Published,
        };
    }
}
