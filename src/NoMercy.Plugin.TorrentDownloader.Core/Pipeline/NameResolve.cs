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
/// What the pool already holds for a cycle's episodes, read once at the start.
/// </summary>
/// <remarks>
/// Read once because it is a file on a disk the media server is also using;
/// added to as the cycle asks the sources about each episode in turn, so an
/// answer given for one episode is there for the next.
/// </remarks>
public sealed class PooledNames
{
    private readonly Dictionary<string, List<string>> _byKey;

    internal PooledNames(Dictionary<string, List<string>> byKey)
    {
        _byKey = byKey;
    }

    /// <summary>Whether any of these keys has a name in the pool.</summary>
    internal bool Has(IEnumerable<string> keys)
    {
        return keys.Any(_byKey.ContainsKey);
    }

    /// <summary>Every name under any of these keys, once each.</summary>
    internal IReadOnlyList<string> Titles(IEnumerable<string> keys)
    {
        return
        [
            .. keys
                .SelectMany(key => _byKey.TryGetValue(key, out List<string>? titles) ? titles : [])
                .Distinct(StringComparer.Ordinal),
        ];
    }

    /// <summary>Keeps what a source has just answered, for this cycle's later episodes.</summary>
    internal void Add(IEnumerable<PooledName> names)
    {
        foreach (PooledName name in names)
        {
            if (!_byKey.TryGetValue(name.Key, out List<string>? titles))
            {
                titles = [];
                _byKey[name.Key] = titles;
            }

            if (!titles.Contains(name.Title, StringComparer.Ordinal))
            {
                titles.Add(name.Title);
            }
        }
    }
}

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

    /// <param name="episodes">The gaps to find names for.</param>
    /// <param name="profile">
    /// What the owner will accept. Its resolution goes into the question a
    /// source is asked, because a source asked about a season answers with the
    /// season.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<ResolvedNames>> ResolveAsync(
        IReadOnlyList<TrackedEpisode> episodes,
        Profile profile,
        CancellationToken ct)
    {
        PooledNames pooled = await FromPoolAsync(episodes, ct);

        // Every episode the pool cannot answer for, asked about on its own.
        //
        // **It was grouped by season, and that is what hid the releases the
        // owner wanted.** One question per season looked like a saving — forty
        // requests down to six — but a source asked about a season answers with
        // the season. Measured on 11 September 2026: srrDB asked
        // `south-park-s15` answers 286 releases and hands back the first 45,
        // which are DVD rips and a making-of documentary and not one release of
        // the episode; asked `south-park-s15e12-1080p` it answers four, one of
        // them `South.Park.S15E12.1080p.BluRay.x264-FilmHD`. The release the
        // owner wanted was at the source the whole time and the question buried
        // it.
        TrackedEpisode[] missing = [.. episodes.Where(episode => !pooled.Has(Keys(episode)))];

        if (missing.Length > 0)
        {
            IReadOnlyList<PooledName> found = await AskAsync(missing, profile, ct);

            // Written before they are used, so the next cycle starts from them
            // even if this one is interrupted.
            await pool.AddAsync(found, ct);

            pooled.Add(found);
        }

        return [.. episodes.Select(episode => new ResolvedNames(episode.Key, pooled.Titles(AllKeys(episode))))];
    }

    /// <summary>
    /// What the pool already knows about these episodes, in one read.
    /// </summary>
    /// <remarks>
    /// One question for the whole cycle rather than one per episode: the store
    /// is a file on a disk the media server is also using.
    /// </remarks>
    public async Task<PooledNames> FromPoolAsync(IReadOnlyList<TrackedEpisode> episodes, CancellationToken ct)
    {
        IReadOnlyList<PooledName> pooled = await pool.ForAsync(
            [.. episodes.SelectMany(AllKeys).Distinct(StringComparer.Ordinal)],
            ct);

        return new(Group(pooled));
    }

    /// <summary>
    /// The names for one episode: from the pool, or from the sources when the
    /// pool has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>One episode at a time, so the episode can be downloaded the
    /// moment it is decided.</strong> The owner's rule of 11 September 2026:
    /// an episode whose torrent has been found is handed to the client at
    /// once, and the run carries on with the next. Asking the sources about
    /// every episode before searching for any held the first download back
    /// until the last episode's name had been asked for — once the sources
    /// were asked per episode, with each one pacing itself, that was most of
    /// an hour on a cycle whose pool was empty.
    /// </para>
    /// <para>
    /// The questions are the same ones, asked in the same way; only the moment
    /// each is asked has moved. What one episode's answer brings in is kept in
    /// <paramref name="pooled"/>, so an episode it also answers for is not
    /// asked about again.
    /// </para>
    /// </remarks>
    public async Task<IReadOnlyList<string>> NamesForAsync(
        TrackedEpisode episode,
        PooledNames pooled,
        Profile profile,
        CancellationToken ct)
    {
        if (!pooled.Has(Keys(episode)))
        {
            IReadOnlyList<PooledName> found = await AskAsync([episode], profile, ct);

            // Written before they are used, so the next cycle starts from them
            // even if this one is interrupted.
            await pool.AddAsync(found, ct);

            pooled.Add(found);
        }

        return pooled.Titles(AllKeys(episode));
    }

    /// <summary>
    /// Asks every name database about every episode that needs one.
    /// </summary>
    private async Task<IReadOnlyList<PooledName>> AskAsync(
        IReadOnlyList<TrackedEpisode> episodes,
        Profile profile,
        CancellationToken ct)
    {
        SourceDefinition[] databases = [.. catalogue.For(SourceRole.Names)];

        Lock guard = new();
        List<PooledName> found = [];

        await Parallel.ForEachAsync(
            episodes,
            new ParallelOptions { MaxDegreeOfParallelism = AtOnce, CancellationToken = ct },
            async (episode, token) =>
            {
                string subject = $"{episode.ShowTitle} {episode.Key}";

                // What it is doing, not only what it is doing it to. This stage
                // is the long one on a cycle whose pool is empty — every source
                // paces itself — and a dashboard that says only the episode
                // leaves the owner watching a line that does not move.
                journal.Started(
                    ActivityStage.Names,
                    subject,
                    $"asking {databases.Length} sources what it is called");

                PooledName[] names =
                [
                    .. (await Task.WhenAll(
                            databases.Select(one => ClimbAsync(one, episode, profile, subject, token))))
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

    /// <summary>
    /// One source, asked about one episode until it answers.
    /// </summary>
    /// <remarks>
    /// The owner's quality first, because the answer to that question is every
    /// release they would take and nothing else. Where a source has none in
    /// that quality it is asked without, and what comes back is refused one
    /// step later — where every spelling of a codec is understood.
    /// </remarks>
    private async Task<PooledName[]> ClimbAsync(
        SourceDefinition database,
        TrackedEpisode episode,
        Profile profile,
        string subject,
        CancellationToken ct)
    {
        foreach (string term in Terms(episode, profile))
        {
            PooledName[] names = await AskOneAsync(database, term, subject, ct);

            if (names.Length > 0)
            {
                return names;
            }
        }

        return [];
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
    private static IEnumerable<string> Terms(TrackedEpisode episode, Profile profile)
    {
        string quality = profile.MaximumResolution.Trim();
        string wanted = quality.Length > 0 ? $" {quality}" : string.Empty;

        // The episode, with the owner's quality and then without it.
        if (wanted.Length > 0)
        {
            yield return $"{episode.ShowTitle} {episode.Key}{wanted}";
        }

        yield return $"{episode.ShowTitle} {episode.Key}";

        // A show whose title is one word is asked under its year as well.
        // Sugar answers with a documentary about beekeeping and Sugar 2024
        // answers with the programme; the four shows in the real library that
        // need this — Lucky, Sugar, Lioness and Silo — are all one word.
        bool oneWord = !episode.ShowTitle.Trim().Contains(' ', StringComparison.Ordinal);

        if (oneWord && episode.ShowYear is int year)
        {
            if (wanted.Length > 0)
            {
                yield return $"{episode.ShowTitle} {year} {episode.Key}{wanted}";
            }

            yield return $"{episode.ShowTitle} {year} {episode.Key}";
        }

        if (episode.Absolute is int absolute)
        {
            // An absolute-numbered release carries no season tag at all, so the
            // forms above find none of them.
            if (wanted.Length > 0)
            {
                yield return $"{episode.ShowTitle} {absolute}{wanted}";
            }

            yield return $"{episode.ShowTitle} {absolute}";
        }
    }

    /// <summary>
    /// Every key that <em>answers</em> for this episode.
    /// </summary>
    /// <remarks>
    /// Two for anime, because the same episode is posted under both forms and
    /// neither can be worked out from the other. A season pack is deliberately
    /// not among them — see <see cref="AllKeys"/>.
    /// </remarks>
    private static IEnumerable<string> Keys(TrackedEpisode episode)
    {
        yield return PoolKey.For(episode.ShowTitle, episode.Key.Season, episode.Key.Number);

        if (episode.Absolute is int absolute)
        {
            yield return PoolKey.ForAbsolute(episode.ShowTitle, absolute);
        }
    }

    /// <summary>
    /// Every key worth reading for this episode, the season's pack included.
    /// </summary>
    /// <remarks>
    /// A pack is a <em>candidate</em> and never an answer, and the difference
    /// is the whole reason there are two of these. It is a candidate because
    /// the harvest files a pack under its season and nothing else would ever
    /// look there, so the pack rules could not be reached at all. It is not an
    /// answer because whether a pack is worth taking depends on how many gaps
    /// that season has — and if a single pack sitting in the pool counted as
    /// having answered, an episode in a season with one gap would never be
    /// asked about again.
    /// </remarks>
    private static IEnumerable<string> AllKeys(TrackedEpisode episode)
    {
        foreach (string key in Keys(episode))
        {
            yield return key;
        }

        yield return PoolKey.ForSeason(episode.ShowTitle, episode.Key.Season);
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
