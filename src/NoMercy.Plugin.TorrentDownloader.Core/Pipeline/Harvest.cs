using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Reads every feed whole and keeps the release names it finds.
/// </summary>
/// <remarks>
/// <para>
/// A feed is read at its own address and never asked a question. <strong>A2:</strong>
/// 0.3.4 put a feed in the search set and asked it once per episode — forty
/// identical requests a cycle, each answering with the same newest twenty
/// posts. Every one of them succeeded, which is why it went unnoticed for a
/// release.
/// </para>
/// <para>
/// What it produces is a pool of names, not a decision. Whether a name is worth
/// having is the profile's business and happens later; harvesting a name the
/// profile will refuse costs nothing, and not harvesting it costs the episode.
/// </para>
/// </remarks>
public sealed class Harvest(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    INamePool pool,
    IActivityJournal journal,
    TimeProvider time)
{
    /// <summary>
    /// Reads every feed and answers how many names were kept.
    /// </summary>
    /// <remarks>
    /// Every feed at once. Read one after another a cycle costs the sum of the
    /// slowest sites; the gate is the only thing here that slows anything down,
    /// and it does that per host.
    /// </remarks>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        SourceDefinition[] feeds = [.. catalogue.For(SourceRole.Feed)];

        PooledName[][] harvested = await Task.WhenAll(feeds.Select(feed => ReadAsync(feed, ct)));

        // Deduped across the feeds as well as within one: the same scene name
        // is on every feed that carries the show, and it is one name.
        PooledName[] names =
        [
            .. harvested
                .SelectMany(feed => feed)
                .GroupBy(name => (name.Key, name.Title))
                .Select(same => same.First()),
        ];

        if (names.Length > 0)
        {
            // Written before anything reads it, so a restart between the
            // harvest and the search starts from these names rather than
            // asking every feed all over again.
            await pool.AddAsync(names, ct);
        }

        return names.Length;
    }

    private async Task<PooledName[]> ReadAsync(SourceDefinition feed, CancellationToken ct)
    {
        journal.Started(ActivityStage.Harvest, feed.Name);

        try
        {
            // Its own address, never its search address. That is the whole of
            // A2, and it is one line because the rule is one line.
            FetchResult result = await fetch.GetAsync(new(feed.Url), feed.Gated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Harvest, feed.Name, failure.ToString());

                return [];
            }

            ISourceReader? reader = readers.For(feed);

            if (reader is null)
            {
                journal.Failed(
                    ActivityStage.Harvest,
                    feed.Name,
                    $"It answered and nothing here reads a source of kind '{feed.Kind}'.");

                return [];
            }

            DateTimeOffset seen = time.GetUtcNow();

            PooledName[] names =
            [
                .. reader.Read(result.Body!, new(feed.Url))
                    .Select(row => (Row: row, Key: PoolKey.Of(ReleaseName.Parse(row.Title))))
                    // A name that answers for no episode is dropped here. Half
                    // a scene feed is films, and a film kept under no slot sits
                    // in the pool for ever being compared against everything.
                    .Where(named => named.Key is not null)
                    .Select(named => new PooledName(named.Key!, named.Row.Title, feed.Name, seen)),
            ];

            journal.Finished(ActivityStage.Harvest, feed.Name, $"{names.Length} names");

            return names;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One site is one site. 0.3.4 read its feeds in turn inside one try
            // block, so the first refusal ended the pass and every feed after
            // it went unread with nothing on the page to say so. Cancellation
            // is not caught: that is the plugin shutting down, not a feed
            // failing.
            journal.Failed(ActivityStage.Harvest, feed.Name, exception.Message);

            return [];
        }
    }
}
