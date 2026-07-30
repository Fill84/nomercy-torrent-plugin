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
                catch (Exception error) when (error is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    harvested[index] = [];
                    string reason = error is IndexerException
                        ? error.Message
                        : $"{error.GetType().Name}: {error.Message}";

                    lock (failures)
                    {
                        failures.Add(new IndexerFailure(paced.Indexer.Name, reason));
                    }

                    log?.Invoke($"{paced.Indexer.Name}: {reason}");
                }
            })
        );

        return new AggregateResult(Deduplicate(harvested), failures);
    }

    private static IReadOnlyList<ReleaseInfo> Deduplicate(IEnumerable<List<ReleaseInfo>> harvested)
    {
        Dictionary<string, int> slots = [];
        List<ReleaseInfo> best = [];

        foreach (ReleaseInfo release in harvested.SelectMany(list => list))
        {
            string titleKey = "t:" + TitleMatcher.Normalize(release.Title);
            string? hashKey = release.InfoHash is string hash ? "h:" + hash.ToLowerInvariant() : null;

            int? existingSlot = null;

            if (hashKey is not null && slots.TryGetValue(hashKey, out int fromHash))
                existingSlot = fromHash;
            else if (slots.TryGetValue(titleKey, out int fromTitle))
                existingSlot = fromTitle;

            int slot;

            if (existingSlot is int found)
            {
                slot = found;
                if (Prefer(release, best[slot]))
                    best[slot] = release;
            }
            else
            {
                slot = best.Count;
                best.Add(release);
            }

            slots[titleKey] = slot;
            if (hashKey is not null)
                slots[hashKey] = slot;
        }

        return best;
    }

    private static bool Prefer(ReleaseInfo candidate, ReleaseInfo existing)
    {
        bool candidateGrabbable = IsGrabbable(candidate);
        bool existingGrabbable = IsGrabbable(existing);

        return candidateGrabbable != existingGrabbable
            ? candidateGrabbable
            : candidate.IndexerPriority > existing.IndexerPriority;
    }

    private static bool IsGrabbable(ReleaseInfo release) =>
        release.MagnetUri is not null || release.DownloadUrl is not null;
}
