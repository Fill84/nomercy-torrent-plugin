// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

public record PacedIndexer(IIndexer Indexer, IndexerPacer Pacer);

public record IndexerFailure(string IndexerName, string Reason);

public record AggregateResult(
    IReadOnlyList<ReleaseInfo> Releases,
    IReadOnlyList<IndexerFailure> Failures
);

public sealed class IndexerAggregator(IReadOnlyList<PacedIndexer> indexers, Action<string>? log = null)
{
    public async Task<AggregateResult> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        List<ReleaseInfo>[] harvested = new List<ReleaseInfo>[indexers.Count];
        List<IndexerFailure> failures = [];

        await Task.WhenAll(
            indexers.Select(async (paced, index) =>
            {
                try
                {
                    IReadOnlyList<ReleaseInfo> found = await paced.Pacer.RunAsync(
                        token => paced.Indexer.SearchAsync(query, token),
                        ct
                    );
                    harvested[index] = [.. found];
                }
                catch (IndexerException error)
                {
                    harvested[index] = [];
                    lock (failures)
                    {
                        failures.Add(new IndexerFailure(paced.Indexer.Name, error.Message));
                    }
                    log?.Invoke($"{paced.Indexer.Name}: {error.Message}");
                }
            })
        );

        return new AggregateResult(Deduplicate(harvested), failures);
    }

    private static IReadOnlyList<ReleaseInfo> Deduplicate(IEnumerable<List<ReleaseInfo>> harvested)
    {
        Dictionary<string, ReleaseInfo> best = [];

        foreach (ReleaseInfo release in harvested.SelectMany(list => list))
        {
            string key = release.InfoHash is string hash
                ? "h:" + hash.ToLowerInvariant()
                : "t:" + TitleMatcher.Normalize(release.Title);

            if (
                !best.TryGetValue(key, out ReleaseInfo? existing)
                || release.IndexerPriority > existing.IndexerPriority
            )
                best[key] = release;
        }

        return [.. best.Values];
    }
}
