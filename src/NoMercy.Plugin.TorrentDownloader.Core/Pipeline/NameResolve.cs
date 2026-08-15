using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// What one episode's release is called, as far as anything knows.
/// </summary>
/// <param name="Episode">Which episode.</param>
/// <param name="Titles">
/// Every name found for it, unjudged. Whether any of them is worth having is
/// the profile's business and happens next.
/// </param>
public sealed record ResolvedNames(EpisodeKey Episode, IReadOnlyList<string> Titles);

/// <summary>
/// Works out what to search the indexers for.
/// </summary>
/// <remarks>
/// <para>
/// The pool first, and the name databases only for what it cannot answer.
/// <strong>A3:</strong> an indexer is asked a full release name, never
/// <c>Silo S03E06</c> — that sometimes worked, which is worse than never
/// working, because the times it did hid the times it did not.
/// </para>
/// <para>
/// <strong>A4:</strong> what a name is called is asked of feeds and name
/// databases only. An indexer answers who is serving a release, which is a
/// different question, and asking it this one is how backfill in 0.3.4 came to
/// depend on the sites least able to answer it.
/// </para>
/// </remarks>
public sealed class NameResolve(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    INamePool pool,
    IActivityJournal journal,
    TimeProvider time)
{
    /// <summary>
    /// How many seasons are asked about at once.
    /// </summary>
    /// <remarks>
    /// From docs/03-architecture.md § Degrees of parallelism. The gate is what
    /// really paces this — one name database is one host — so the number only
    /// decides how many are queued behind it.
    /// </remarks>
    public static int AtOnce => Math.Min(8, Environment.ProcessorCount);

    public async Task<IReadOnlyList<ResolvedNames>> ResolveAsync(
        IReadOnlyList<TrackedEpisode> episodes,
        CancellationToken ct)
    {
        // One question for the whole cycle rather than one per episode: the
        // store is a file on a disk the media server is also using.
        IReadOnlyList<PooledName> pooled = await pool.ForAsync(
            [.. episodes.SelectMany(Keys).Distinct(StringComparer.Ordinal)],
            ct);

        Dictionary<string, List<string>> byKey = Group(pooled);

        // A season nothing in the pool answers for. Grouped, because the answer
        // to "what is season three called" covers every episode of it — asking
        // per episode is forty requests where six will do.
        (int ShowId, int Season)[] missing =
        [
            .. episodes
                .Where(episode => Keys(episode).All(key => !byKey.ContainsKey(key)))
                .Select(episode => (episode.Key.ShowId, episode.Key.Season))
                .Distinct(),
        ];

        if (missing.Length > 0)
        {
            IReadOnlyList<PooledName> found = await AskAsync(missing, episodes, ct);

            // Written before they are used, so the next cycle starts from them
            // even if this one is interrupted.
            await pool.AddAsync(found, ct);

            foreach (PooledName name in found)
            {
                if (!byKey.TryGetValue(name.Key, out List<string>? titles))
                {
                    titles = [];
                    byKey[name.Key] = titles;
                }

                if (!titles.Contains(name.Title, StringComparer.Ordinal))
                {
                    titles.Add(name.Title);
                }
            }
        }

        return
        [
            .. episodes.Select(episode => new ResolvedNames(
                episode.Key,
                [
                    .. Keys(episode)
                        .SelectMany(key => byKey.TryGetValue(key, out List<string>? titles) ? titles : [])
                        .Distinct(StringComparer.Ordinal),
                ])),
        ];
    }

    /// <summary>
    /// Asks every name database about every season that needs one.
    /// </summary>
    private async Task<IReadOnlyList<PooledName>> AskAsync(
        IReadOnlyList<(int ShowId, int Season)> seasons,
        IReadOnlyList<TrackedEpisode> episodes,
        CancellationToken ct)
    {
        SourceDefinition[] databases = [.. catalogue.For(SourceRole.Names)];

        Lock guard = new();
        List<PooledName> found = [];

        await Parallel.ForEachAsync(
            seasons,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct },
            async (season, token) =>
            {
                TrackedEpisode one = episodes.First(episode =>
                    episode.Key.ShowId == season.ShowId && episode.Key.Season == season.Season);

                string subject = $"{one.ShowTitle} S{season.Season:00}";

                journal.Started(ActivityStage.Names, subject);

                PooledName[] names =
                [
                    .. (await Task.WhenAll(
                            from database in databases
                            from term in Terms(one)
                            select AskOneAsync(database, term, subject, token)))
                        .SelectMany(answer => answer),
                ];

                lock (guard)
                {
                    found.AddRange(names);
                }

                journal.Finished(ActivityStage.Names, subject, $"{names.Length} names");
            });

        return found;
    }

    private async Task<PooledName[]> AskOneAsync(
        SourceDefinition database,
        string term,
        string subject,
        CancellationToken ct)
    {
        Uri address = new(Query.Write(database.SearchAddress!, term, database.Query));

        try
        {
            FetchResult result = await fetch.GetAsync(address, database.SearchAddressGated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Names, $"{subject} · {database.Name}", failure.ToString());

                return [];
            }

            ISourceReader? reader = readers.For(database);

            if (reader is null)
            {
                journal.Failed(
                    ActivityStage.Names,
                    $"{subject} · {database.Name}",
                    $"It answered and nothing here reads a source of kind '{database.Kind}'.");

                return [];
            }

            DateTimeOffset seen = time.GetUtcNow();

            return
            [
                .. reader.Read(result.Body!, address)
                    .Select(row => (row.Title, Key: PoolKey.Of(ReleaseName.Parse(row.Title))))
                    .Where(named => named.Key is not null)
                    .Select(named => new PooledName(named.Key!, named.Title, database.Name, seen)),
            ];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One name database is one name database, exactly as one feed is
            // one feed. The episode is worth more than the site that failed.
            journal.Failed(ActivityStage.Names, $"{subject} · {database.Name}", exception.Message);

            return [];
        }
    }

    /// <summary>
    /// What to ask about this show and season.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A show whose title is one word is asked under its year as well.
    /// <em>Sugar</em> answers with a documentary about beekeeping and
    /// <em>Sugar 2024</em> answers with the programme; the four shows in the
    /// real library that need this — Lucky, Sugar, Lioness and Silo — are all
    /// one word, and adding the year to every show would double every request
    /// for nothing.
    /// </para>
    /// <para>
    /// Anime is asked under the bare title as well, because an
    /// absolute-numbered release carries no season tag at all: the number after
    /// the separator is counted from the start of the programme, so
    /// <c>Show S01</c> finds none of them.
    /// </para>
    /// </remarks>
    private static IEnumerable<string> Terms(TrackedEpisode episode)
    {
        string season = $"S{episode.Key.Season:00}";

        yield return $"{episode.ShowTitle} {season}";

        bool oneWord = !episode.ShowTitle.Trim().Contains(' ', StringComparison.Ordinal);

        if (oneWord && episode.ShowYear is int year)
        {
            yield return $"{episode.ShowTitle} {year} {season}";
        }

        if (episode.Kind == LibraryKind.Anime)
        {
            yield return episode.ShowTitle;
        }
    }

    /// <summary>
    /// Every key this episode could be answered under.
    /// </summary>
    /// <remarks>
    /// Two for anime, because the same episode is posted under both forms and
    /// neither can be worked out from the other.
    /// </remarks>
    private static IEnumerable<string> Keys(TrackedEpisode episode)
    {
        yield return PoolKey.For(episode.ShowTitle, episode.Key.Season, episode.Key.Number);

        if (episode.Absolute is int absolute)
        {
            yield return PoolKey.ForAbsolute(episode.ShowTitle, absolute);
        }
    }

    private static Dictionary<string, List<string>> Group(IReadOnlyList<PooledName> names)
    {
        Dictionary<string, List<string>> byKey = new(StringComparer.Ordinal);

        foreach (PooledName name in names)
        {
            if (!byKey.TryGetValue(name.Key, out List<string>? titles))
            {
                titles = [];
                byKey[name.Key] = titles;
            }

            if (!titles.Contains(name.Title, StringComparer.Ordinal))
            {
                titles.Add(name.Title);
            }
        }

        return byKey;
    }
}
