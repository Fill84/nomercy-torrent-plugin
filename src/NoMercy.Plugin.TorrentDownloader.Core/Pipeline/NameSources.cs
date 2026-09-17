using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>A release name, and the name source that gave it.</summary>
/// <param name="Title">The release name exactly as the source printed it.</param>
/// <param name="Source">The catalogue entry that gave it, for the Activity page.</param>
/// <param name="Published">When the source published it, where it says.</param>
public sealed record SourceName(string Title, string Source, DateTimeOffset? Published = null)
{
    /// <summary>How long before an episode aired a name for it may be published.</summary>
    /// <remarks>
    /// A week, not a day: a library's air date is one country's, a release can be out a day or two before
    /// another's, and what this is for is years — the 2015 Dark Matter's S02E04, published in July 2016, taken
    /// for the 2024 programme's S02E04 of 17 September 2026.
    /// </remarks>
    public static readonly TimeSpan Before = TimeSpan.FromDays(7);

    /// <summary>
    /// Whether this name was published too long before the episode aired to be a name for it.
    /// </summary>
    /// <remarks>
    /// Two programmes can share a title, and their release names differ by no more than the year one of them
    /// carries — which an indexer then drops from its titles. When a name was published is what cannot be the
    /// same. A name or an episode with no date is not judged by this.
    /// </remarks>
    public bool PublishedBeforeItAired(TrackedEpisode episode)
    {
        return PublishedBeforeItAired(Published, episode);
    }

    /// <summary>
    /// Whether something published at <paramref name="published"/> was published too long before the episode
    /// aired to be of it.
    /// </summary>
    /// <remarks>
    /// One rule for a name and for an indexer's row, so the week is the same week for both. No date on either
    /// side is not judged by this.
    /// </remarks>
    public static bool PublishedBeforeItAired(DateTimeOffset? published, TrackedEpisode episode)
    {
        return published is DateTimeOffset at
               && episode.AirDate is DateOnly aired
               && at < new DateTimeOffset(aired.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) - Before;
    }

    /// <summary>The names published too long before the episode aired, by whichever source dated them.</summary>
    /// <remarks>
    /// A name is published once, so one source's date is the date of every copy of it. Judged copy by copy, a
    /// name one source dated and another did not survived on the undated copy: the 2015 Dark Matter's FaiLED
    /// release, dated 2016 by PreDB.net and by nothing else PreDB gave, was grabbed twice more for the 2024
    /// programme on 17 September 2026 after this rule was in.
    /// </remarks>
    public static IReadOnlySet<string> TooOld(IEnumerable<SourceName> names, TrackedEpisode episode)
    {
        return new HashSet<string>(
            names.Where(name => name.PublishedBeforeItAired(episode)).Select(name => name.Title),
            StringComparer.Ordinal);
    }
}

/// <summary>The feed release names a run took, per episode, and the name sources that failed in it.</summary>
public sealed class FeedNamesTaken(IReadOnlyDictionary<EpisodeKey, IReadOnlyList<SourceName>> byEpisode)
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _failed = new(StringComparer.OrdinalIgnoreCase);

    private readonly System.Collections.Concurrent.ConcurrentDictionary<EpisodeKey, bool> _namesakes = new();

    /// <summary>Whether a name source answered this episode with names another programme of its title published.</summary>
    /// <remarks>
    /// Names published long before the episode aired are the proof that an older programme shares the title:
    /// Dark Matter S02E04 is answered with the 2015 programme's names from 2016 to 2023. Where that is so, a
    /// result that carries no date cannot be told apart from that programme's.
    /// </remarks>
    public bool HasNamesake(EpisodeKey episode)
    {
        return _namesakes.ContainsKey(episode);
    }

    /// <summary>Writes down that this episode's names include an older programme's of the same title.</summary>
    public void Namesake(EpisodeKey episode)
    {
        _namesakes[episode] = true;
    }

    /// <summary>No names, and nothing failed: a fresh one each time, because a run writes into it.</summary>
    public static FeedNamesTaken None => new(new Dictionary<EpisodeKey, IReadOnlyList<SourceName>>());

    /// <summary>Whether this name source failed earlier in the run, and so gives no names in it.</summary>
    public bool Failed(string source)
    {
        return _failed.ContainsKey(source);
    }

    /// <summary>
    /// A name source failed: <c>run.md</c> — it gives no release names in that run, so it is asked nothing more.
    /// </summary>
    public void Fail(string source)
    {
        _failed[source] = true;
    }

    /// <summary>Every name taken, for every episode.</summary>
    public IEnumerable<SourceName> All => byEpisode.Values.SelectMany(names => names);

    /// <summary>The names taken for one episode, none when no feed named it.</summary>
    public IReadOnlyList<SourceName> For(EpisodeKey episode)
    {
        return byEpisode.GetValueOrDefault(episode) ?? [];
    }
}

/// <summary>Where a run's release names come from.</summary>
/// <remarks>
/// An interface so the search cycle's own tests can hand it names without a site behind them; the
/// plugin has one answer, <see cref="NameSources"/>.
/// </remarks>
public interface IReleaseNames
{
    /// <summary>Reads every feed at once and takes what names an episode being searched for.</summary>
    Task<FeedNamesTaken> ReadFeedsAsync(IReadOnlyList<TrackedEpisode> episodes, CancellationToken ct);

    /// <summary>One episode's release names: what the feeds gave, or else what the searches give.</summary>
    Task<IReadOnlyList<SourceName>> NamesForAsync(TrackedEpisode episode, FeedNamesTaken taken, CancellationToken ct);
}

/// <summary>
/// The release names of a run, from the four name sources: their feeds, and their searches for an
/// episode no feed named.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/release-names.md</c> and <c>run.md</c>, the owner's requirements of 15 September 2026.
/// A run reads the latest feed of every name source at once before it works on any episode, and takes a
/// feed name for an episode being searched for when it names that show, season and episode. An episode
/// no feed named is looked up by show and episode in every name source's search. Nothing is kept
/// between runs: the name pool is gone, because a feed is read every run and a search asks the source
/// directly.
/// </para>
/// <para>
/// <strong>A2:</strong> a feed is read at its own address and never asked a question; 0.3.4 put one in
/// the search set and asked it the same thing forty times a cycle. <strong>A4:</strong> what a release
/// is called is asked of name sources only, never of an indexer.
/// </para>
/// </remarks>
public sealed class NameSources(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    IActivityJournal journal,
    TimeProvider time,
    ISourceLedger? ledger = null) : IReleaseNames
{
    public async Task<FeedNamesTaken> ReadFeedsAsync(IReadOnlyList<TrackedEpisode> episodes, CancellationToken ct)
    {
        SourceDefinition[] feeds = [.. catalogue.For(SourceRole.Feed)];

        // Every feed at once. Read one after another a run costs the sum of the slowest sites; the gate
        // is the only thing that slows anything, and it does that per host.
        (SourceName[] Names, bool Failed)[] read = await Task.WhenAll(feeds.Select(feed => ReadFeedAsync(feed, ct)));

        HashSet<EpisodeKey> namesakes = [];
        FeedNamesTaken taken = new(Take([.. read.SelectMany(one => one.Names)], episodes, namesakes));

        foreach (EpisodeKey namesake in namesakes)
        {
            taken.Namesake(namesake);
        }

        for (int at = 0; at < feeds.Length; at++)
        {
            if (read[at].Failed)
            {
                taken.Fail(feeds[at].Name);
            }
        }

        return taken;
    }

    public async Task<IReadOnlyList<SourceName>> NamesForAsync(TrackedEpisode episode, FeedNamesTaken taken, CancellationToken ct)
    {
        IReadOnlyList<SourceName> fed = taken.For(episode.Key);

        // Named by a feed, so no search is asked: each is a paced request, and this one has no use.
        if (fed.Count > 0)
        {
            return fed;
        }

        return await LookUpAsync(episode, taken, ct);
    }

    /// <summary>
    /// The feed names that name an episode being searched for, per episode.
    /// </summary>
    /// <remarks>
    /// Only an episode that has aired: one still to come is not searched for, and a name posted for it
    /// early is not taken. Each name once per source — the same release is on three feeds, and it is
    /// kept from each so the page can say where it came from.
    /// </remarks>
    private static Dictionary<EpisodeKey, IReadOnlyList<SourceName>> Take(
        IReadOnlyList<SourceName> names,
        IReadOnlyList<TrackedEpisode> episodes,
        HashSet<EpisodeKey> namesakes)
    {
        TrackedEpisode[] searched = [.. episodes.Where(episode => episode.State == EpisodeState.Missing)];
        Dictionary<EpisodeKey, IReadOnlyList<SourceName>> taken = [];

        foreach (TrackedEpisode episode in searched)
        {
            SourceName[] naming =
            [
                .. names
                    .Where(name => EpisodeNaming.Names(ReleaseName.Parse(name.Title), episode.ShowTitle, episode.Key))
                    .Distinct(),
            ];

            IReadOnlySet<string> tooOld = SourceName.TooOld(naming, episode);
            SourceName[] named = [.. naming.Where(name => !tooOld.Contains(name.Title))];

            if (tooOld.Count > 0)
            {
                namesakes.Add(episode.Key);
            }

            if (named.Length > 0)
            {
                taken[episode.Key] = named;
            }
        }

        return taken;
    }

    private async Task<(SourceName[] Names, bool Failed)> ReadFeedAsync(SourceDefinition feed, CancellationToken ct)
    {
        long started = time.GetTimestamp();

        journal.Started(ActivityStage.Harvest, feed.Name);

        try
        {
            // Its own address, never its search address. That is the whole of A2.
            FetchResult result = await fetch.GetAsync(new(feed.Url), feed.Gated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Harvest, feed.Name, failure.ToString());
                await WroteAsync(feed, started, 0, failure.ToString(), ct);

                return ([], true);
            }

            if (readers.For(feed) is not ISourceReader reader)
            {
                string unread = $"It answered and nothing here reads a source of kind '{feed.Kind}'.";

                journal.Failed(ActivityStage.Harvest, feed.Name, unread);
                await WroteAsync(feed, started, 0, unread, ct);

                return ([], true);
            }

            SourceName[] names = [.. reader.Read(result.Body!, new(feed.Url)).Select(row => new SourceName(row.Title, feed.Name, row.Published))];

            journal.Finished(ActivityStage.Harvest, feed.Name, $"{names.Length} names");
            await WroteAsync(feed, started, names.Length, null, ct);

            return (names, false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One source is one source (run.md): a feed that fails gives nothing this run, and the others
            // are read all the same. Cancellation is not caught: that is Stop, not a feed failing.
            journal.Failed(ActivityStage.Harvest, feed.Name, exception.Message);
            await WroteAsync(feed, started, 0, exception.Message, ct);

            return ([], true);
        }
    }

    /// <summary>
    /// Asks every name source's search about one episode, all at once, and keeps what names it.
    /// </summary>
    /// <remarks>
    /// Asked <c>Show SxxEyy</c> and nothing else (<c>release-names.md</c>: the plugin builds no search term
    /// of its own). No quality, which is judged on the names that come back; no year and no absolute
    /// number, which the search ladder added until 15 September 2026 and the owner's requirements do not.
    /// </remarks>
    private async Task<IReadOnlyList<SourceName>> LookUpAsync(TrackedEpisode episode, FeedNamesTaken taken, CancellationToken ct)
    {
        // Not a source that failed earlier in the run, feed or search: it gives no names in it (run.md).
        SourceDefinition[] searches = [.. catalogue.For(SourceRole.Names).Where(search => !taken.Failed(search.Name))];
        string subject = $"{episode.ShowTitle} {episode.Key}";
        string term = subject;

        journal.Started(ActivityStage.Names, subject, $"asking {searches.Length} sources what it is called");

        SourceName[][] answers = await Task.WhenAll(searches.Select(search => AskAsync(search, episode, term, subject, taken, ct)));
        SourceName[] every = [.. answers.SelectMany(answer => answer).Distinct()];

        // Judged across every source's answer, never one source at a time: any source's date is the name's.
        IReadOnlySet<string> tooOld = SourceName.TooOld(every, episode);
        SourceName[] names = [.. every.Where(name => !tooOld.Contains(name.Title))];

        if (tooOld.Count > 0)
        {
            taken.Namesake(episode.Key);

            journal.Noted(
                ActivityStage.Names,
                subject,
                $"{tooOld.Count} names published more than a week before it aired were left out, from every source that gave them: another programme of the same name");
        }

        journal.Counted(RunCounter.EpisodesAsked);
        journal.Counted(RunCounter.NamesFound, names.Length);
        journal.Finished(ActivityStage.Names, subject, $"{names.Length} names");

        return names;
    }

    private async Task<SourceName[]> AskAsync(
        SourceDefinition search,
        TrackedEpisode episode,
        string term,
        string subject,
        FeedNamesTaken taken,
        CancellationToken ct)
    {
        Uri address = new(Query.Write(search.SearchAddress!, term, search.Query));

        try
        {
            FetchResult result = await fetch.GetAsync(address, search.SearchAddressGated, ct);

            if (result.Failure is FetchFailure failure)
            {
                taken.Fail(search.Name);
                journal.Failed(ActivityStage.Names, $"{subject} · {search.Name}", $"{failure} It gives no names for the rest of this run.");
                journal.Noted(ActivityStage.Names, subject, $"{search.Name} · {term} · {failure}");

                return [];
            }

            if (readers.For(search) is not ISourceReader reader)
            {
                string unread = $"It answered and nothing here reads a source of kind '{search.Kind}'.";

                journal.Failed(ActivityStage.Names, $"{subject} · {search.Name}", unread);
                journal.Noted(ActivityStage.Names, subject, $"{search.Name} · {term} · {unread}");

                return [];
            }

            // A search answers with whatever it thinks is near, other episodes included. Only what names
            // this episode is kept, exactly as for a feed.
            SourceName[] naming =
            [
                .. reader.Read(result.Body!, address)
                    .Where(row => EpisodeNaming.Names(ReleaseName.Parse(row.Title), episode.ShowTitle, episode.Key))
                    .Select(row => new SourceName(row.Title, search.Name, row.Published)),
            ];

            // Every name, dated or not: whether one is too old is judged across every source's answer, since
            // one source's date is the date of every copy.
            SourceName[] names = naming;

            // What the source was asked and what it said, word for word, for the Activity page.
            journal.Noted(ActivityStage.Names, subject, Answered(search, term, names));

            return names;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A search that fails gives nothing this run, and the other sources are still asked (run.md).
            taken.Fail(search.Name);
            journal.Failed(ActivityStage.Names, $"{subject} · {search.Name}", exception.Message);
            journal.Noted(ActivityStage.Names, subject, $"{search.Name} · {term} · {exception.Message}");

            return [];
        }
    }

    /// <summary>One source's answer as a line: what it was asked, and the names it gave.</summary>
    /// <remarks>Five names at most; a line of forty is one nobody reads, so the rest are counted.</remarks>
    private static string Answered(SourceDefinition search, string term, SourceName[] names)
    {
        string[] titles = [.. names.Select(one => one.Title).Distinct(StringComparer.Ordinal)];

        if (titles.Length == 0)
        {
            return $"{search.Name} · {term} · no names";
        }

        string counted = titles.Length == 1 ? "1 name" : $"{titles.Length} names";
        string more = titles.Length > 5 ? $" and {titles.Length - 5} more" : string.Empty;

        return $"{search.Name} · {term} · {counted}: {string.Join(", ", titles.Take(5))}{more}";
    }

    /// <summary>Writes down what one feed answered, for the Sources page.</summary>
    private async Task WroteAsync(SourceDefinition feed, long started, int rows, string? refusal, CancellationToken ct)
    {
        if (ledger is null)
        {
            return;
        }

        await ledger.RecordAsync(new(feed.Name, time.GetUtcNow(), rows, refusal, time.GetElapsedTime(started)), ct);
    }
}
