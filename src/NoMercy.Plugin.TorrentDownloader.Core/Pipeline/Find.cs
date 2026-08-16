using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Asks every indexer who is serving one release, and merges what comes back.
/// </summary>
/// <remarks>
/// <strong>A3:</strong> what goes out is the full release name and nothing
/// else. 0.3.4 searched indexers for <c>Silo S03E06</c>, which sometimes
/// worked — and the times it did hid the times it did not.
/// </remarks>
public sealed class Find(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    IActivityJournal journal)
{
    /// <summary>
    /// Every copy of <paramref name="releaseName"/> anybody is serving.
    /// </summary>
    /// <remarks>
    /// Every indexer at once. Asked one after another, a search costs the sum
    /// of the slowest sites — and there is one search per episode of a cycle.
    /// The gate is the only thing that slows any of it down, and it does that
    /// per host.
    /// </remarks>
    public async Task<IReadOnlyList<ReleaseCopy>> SearchAsync(string releaseName, CancellationToken ct)
    {
        SourceDefinition[] indexers = [.. catalogue.For(SourceRole.Indexer)];

        ReleaseCopy[][] answers = await Task.WhenAll(
            indexers.Select(indexer => AskAsync(indexer, releaseName, ct)));

        return Merge([.. answers.SelectMany(answer => answer)]);
    }

    /// <summary>
    /// Follows a copy to its own page for the magnet the listing did not carry.
    /// </summary>
    /// <remarks>
    /// <strong>C3.</strong> Called for the copy that was chosen and for no
    /// other: following every row of every answer is a request per row per
    /// episode, which is how a plugin gets itself banned from a site. A copy
    /// that already has a magnet is answered as it is, without a request.
    /// </remarks>
    public async Task<ReleaseCopy> FollowAsync(ReleaseCopy chosen, CancellationToken ct)
    {
        if (chosen.Magnet is not null || chosen.DetailUrl is null)
        {
            return chosen;
        }

        SourceDefinition? indexer = catalogue.Enabled
            .FirstOrDefault(source => string.Equals(source.Name, chosen.Source, StringComparison.OrdinalIgnoreCase));

        journal.Started(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", "following the row's own page");

        try
        {
            FetchResult result = await fetch.GetAsync(chosen.DetailUrl, indexer?.Gated ?? false, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", failure.ToString());

                return chosen;
            }

            if (DetailPage.Read(result.Body!, chosen.Title) is not (string magnet, string hash))
            {
                journal.Failed(
                    ActivityStage.Find,
                    $"{chosen.Title} · {chosen.Source}",
                    "Its own page names no torrent either.");

                return chosen;
            }

            journal.Finished(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", "magnet found");

            return chosen with
            {
                Magnet = magnet,
                InfoHash = chosen.InfoHash ?? hash,
                Trackers = [.. chosen.Trackers.Union(Magnets.TrackersOf(magnet), StringComparer.OrdinalIgnoreCase)],
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", exception.Message);

            return chosen;
        }
    }

    /// <summary>
    /// One torrent per info hash, with everything every site knew about it.
    /// </summary>
    /// <remarks>
    /// The highest seeder count, the trackers of all of them, and the site that
    /// had the highest count — which is the one the history line names. A copy
    /// with no hash is left alone: nothing says two rows with the same title
    /// are the same torrent, and merging them would hand one file's trackers to
    /// another.
    /// </remarks>
    public static IReadOnlyList<ReleaseCopy> Merge(IReadOnlyList<ReleaseCopy> copies)
    {
        List<ReleaseCopy> merged = [.. copies.Where(copy => copy.InfoHash is null)];

        foreach (IGrouping<string, ReleaseCopy> same in copies
                     .Where(copy => copy.InfoHash is not null)
                     .GroupBy(copy => copy.InfoHash!, StringComparer.OrdinalIgnoreCase))
        {
            ReleaseCopy best = same
                .OrderByDescending(copy => copy.Seeders ?? 0)
                .ThenByDescending(copy => copy.Priority)
                .First();

            merged.Add(best with
            {
                Trackers =
                [
                    .. same
                        .SelectMany(copy => copy.Trackers.Union(Magnets.TrackersOf(copy.Magnet), StringComparer.OrdinalIgnoreCase))
                        .Distinct(StringComparer.OrdinalIgnoreCase),
                ],
                Magnet = same.Select(copy => copy.Magnet).FirstOrDefault(magnet => magnet is not null),
                DetailUrl = best.DetailUrl ?? same.Select(copy => copy.DetailUrl).FirstOrDefault(url => url is not null),
                SizeBytes = best.SizeBytes ?? same.Select(copy => copy.SizeBytes).FirstOrDefault(size => size is not null),
            });
        }

        return merged;
    }

    private async Task<ReleaseCopy[]> AskAsync(SourceDefinition indexer, string releaseName, CancellationToken ct)
    {
        string subject = $"{releaseName} · {indexer.Name}";

        journal.Started(ActivityStage.Find, subject);

        try
        {
            Uri address = new(Query.Write(indexer.SearchAddress!, releaseName, indexer.Query));

            FetchResult result = await fetch.GetAsync(address, indexer.SearchAddressGated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, subject, failure.ToString());

                return [];
            }

            ISourceReader? reader = readers.For(indexer);

            if (reader is null)
            {
                journal.Failed(
                    ActivityStage.Find,
                    subject,
                    $"It answered and nothing here reads a source of kind '{indexer.Kind}'.");

                return [];
            }

            ReleaseCopy[] copies =
            [
                .. reader.Read(result.Body!, address).Select(row => new ReleaseCopy(
                    row.Title,
                    indexer.Name,
                    indexer.Priority,
                    row.InfoHash ?? Magnets.HashOf(row.Magnet),
                    row.Magnet,
                    row.DetailUrl,
                    row.Seeders,
                    row.SizeBytes,
                    Magnets.TrackersOf(row.Magnet))),
            ];

            journal.Finished(ActivityStage.Find, subject, $"{copies.Length} copies");

            return copies;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One site is one site, exactly as it is in the harvest. An episode
            // is worth more than the indexer that failed on it.
            journal.Failed(ActivityStage.Find, subject, exception.Message);

            return [];
        }
    }
}
