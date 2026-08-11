// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

/// <summary>Searching, behind an interface so a test does not need indexers.</summary>
public interface IReleaseSearch
{
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct);
}

/// <summary>
/// Everything an indexer has posted lately, asked for without a query.
///
/// <para>
/// A feed is not a search. A search asks "who has this episode" and pays a rate limit per
/// question; a feed is handed over whole and costs one request no matter how much of it
/// turns out to be useful. That difference is the whole reason to have both: the search
/// cadence works through the whole backlog and then has nothing left to do, and the feed
/// catches tonight's airing within a quarter of an hour of it being posted, without asking
/// anybody anything.
/// </para>
/// </summary>
public interface IReleaseFeed
{
    Task<IReadOnlyList<ReleaseInfo>> LatestAsync(CancellationToken ct);
}

/// <summary>
/// Choosing between candidates. Wraps the profiles and the scorer, which is the
/// orchestrator's business to call and nobody's business to reimplement.
/// </summary>
public interface IReleaseChooser
{
    /// <summary>
    /// <paramref name="allowSeasonPacks"/> is the caller's decision, not this one's: only
    /// the orchestrator knows how much of the season is missing, and therefore whether a
    /// season's worth of bytes buys one gap or ten.
    /// </summary>
    ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates, bool allowSeasonPacks);
}

/// <summary>Handing a finished download to the server's import pipeline.</summary>
public interface IIntakeHandoff
{
    /// <summary>False when the move did not happen, which leaves the grab unfinished so the next cycle retries it.</summary>
    Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct);
}

public sealed record OrchestratorOptions
{
    /// <summary>
    /// How many downloads may run at once. This is the bound that turns a first run on
    /// a library with years of gaps into a steady stream rather than several hundred
    /// downloads competing for one connection, one disk and one swarm.
    /// </summary>
    public int MaxConcurrentDownloads { get; init; } = 5;


    /// <summary>
    /// How many episodes of a season have to be missing before a season pack is worth
    /// considering.
    ///
    /// <para>
    /// A pack costs a whole season of bytes whether it settles one gap or ten, so below
    /// this the episode release is the cheaper answer by a wide margin. Three is the
    /// point where re-downloading a season stops being obviously worse than three
    /// separate torrents; it is a judgement, which is why it is a setting.
    /// </para>
    /// </summary>
    public int SeasonPackThreshold { get; init; } = 3;

    /// <summary>After this many fruitless searches an episode is parked rather than asked for forever.</summary>
    public int MaxSearchAttempts { get; init; } = 12;

    /// <summary>How long a failed release is skipped before it is worth another try.</summary>
    public TimeSpan BlacklistDuration { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Whether season 0 counts.
    ///
    /// <para>
    /// Off, because specials are where a library's metadata is loosest - recaps,
    /// behind-the-scenes reels, convention panels - and they sort to the front of an
    /// unsearched queue, so the first thing the plugin would ever download is the thing
    /// the owner wanted least. On a real library this was twenty-five Simpsons specials
    /// ahead of every actual episode.
    /// </para>
    /// </summary>
    public bool IncludeSpecials { get; init; }

    /// <summary>
    /// Shows to follow even though nothing of them is on the server yet.
    ///
    /// <para>
    /// The counterpart of the shelf rule. Without it the plugin can only ever finish a
    /// show somebody already started by hand, and can never begin one - which is a
    /// perfectly coherent tool and not the one anybody wants.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> FollowedShowIds { get; init; } = [];

    public required string DownloadFolder { get; init; }

    /// <summary>
    /// Trackers added to every torrent, on top of whatever the indexers named for it.
    ///
    /// <para>
    /// The aggregator already merges the trackers every indexer reported for one info hash,
    /// which is the bigger half of the swarm. These are the owner's own, and they go on
    /// everything rather than only on the magnets this plugin had to build - a torrent
    /// found through one site announces to that site's trackers alone, and the peers on the
    /// others are simply never asked.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ExtraTrackers { get; init; } = [];

    /// <summary>
    /// How much room to leave on the disk after a download finishes.
    ///
    /// <para>
    /// A media server that fills its own disk does not politely stop downloading - it
    /// stops encoding, stops writing databases, and stops playing back, all at once and
    /// for reasons that look nothing like a torrent. Twenty gigabytes is roughly one
    /// oversized film of headroom, and cheap insurance against a plugin that would
    /// otherwise cheerfully take the last byte.
    /// </para>
    /// </summary>
    public long MinimumFreeBytes { get; init; } = 20L * 1024 * 1024 * 1024;
}

/// <summary>
/// What one refresh concluded.
///
/// <para>
/// More than a count, because a plugin that decides to want nothing has to be able to say
/// why. A library of sixty-seven shows can produce "0 episodes wanted" for two entirely
/// different reasons, and an owner reading only the zero concludes the thing is broken.
/// </para>
/// </summary>
/// <param name="Shows">How many the plugin is working on.</param>
/// <param name="NotOnTheServer">
/// Shows the library lists with no episode on the server. Rows from a metadata provider,
/// not media anybody has - and not this plugin's business unless it is asked for one by
/// name.
/// </param>
/// <param name="Finished">Shows that are on the server and have ended or been cancelled, so nothing more of them will ever exist.</param>
public sealed record WantedRefresh(int Wanted, int Shows, int NotOnTheServer, int Finished);

/// <summary>
/// What one pass over the feeds came to.
///
/// <para>
/// <paramref name="Matched"/> is reported beside <paramref name="Grabbed"/> because the
/// two failures look identical from outside and have nothing to do with each other. A feed
/// that matched nothing is a feed carrying other people's shows, or an indexer that is not
/// answering. A feed that matched plenty and grabbed none of it is working perfectly and
/// being turned down - by the quality profile, or because the items link to a web page
/// instead of to a torrent, which some sites' feeds do. Only one of those is worth
/// anybody's evening.
/// </para>
/// </summary>
public sealed record FeedCycle(int Matched, int Grabbed);

/// <summary>
/// What one pass over the engine came to.
/// </summary>
/// <param name="Imported">Downloads handed to the intake.</param>
/// <param name="PutBack">
/// Episodes a failed download returned to the queue.
///
/// <para>
/// Reported so the caller can search for them at once instead of at the next cadence. A
/// failed download is not a state an episode rests in - the episode is simply missing
/// again, exactly as it was before anything was grabbed, and the answer is the same answer:
/// look for another release. Waiting six hours to do that is the plugin sitting on work it
/// already knows about.
/// </para>
/// </param>
public sealed record TransfersCycle(int Imported, int PutBack);

/// <summary>
/// What one pass of the search cadence came to.
///
/// <para>
/// <paramref name="Searched"/> is reported beside <paramref name="Grabbed"/> because a
/// cadence that asks nothing and one that asks and is turned down are the same silence from
/// outside, and only one of them is a bug. The plugin spent a day in the first state - a
/// batch of ten filled entirely with unaired episodes, refetched and re-skipped every five
/// minutes - and nothing anywhere said so.
/// </para>
/// </summary>
public sealed record SearchCycle(int Searched, int Grabbed);

/// <summary>Whether a pasted link was taken, and what to tell the person who pasted it.</summary>
public sealed record ManualAdd(bool Added, string Message);

/// <summary>
/// The loop: notice what is missing, find something for it, hand it to the engine,
/// and put the result where the server will import it.
///
/// <para>
/// Each phase is a separate method rather than one long cycle, because they run on
/// different triggers - a library change refreshes, a cron searches, and transfers are
/// polled - and because a test that wants to prove one of them should not have to
/// arrange the other two.
/// </para>
/// </summary>
public sealed class DownloadOrchestrator(
    ILibraryQuery library,
    IDownloadStore store,
    IReleaseSearch search,
    IReleaseChooser chooser,
    ITorrentEngine engine,
    IIntakeHandoff intake,
    OrchestratorOptions options,
    PrivateTrackerRegistry privateTrackers,
    Func<DateTimeOffset> now,
    IReleaseFeed? feed = null,
    Func<string, long?>? freeSpace = null,
    IReleaseResolver? resolver = null)
{
    /// <summary>
    /// Brings the wanted list in line with the library.
    ///
    /// <para>
    /// Two rules decide what the plugin holds, and both are about the difference between a
    /// catalogue and a shelf. The library lists every show its metadata provider knows
    /// about; the owner's server holds a much smaller set of them.
    /// </para>
    ///
    /// <para>
    /// <b>At least one episode has to be on the server.</b> A show with none is a row left
    /// behind - an "add content" nobody followed through on, a folder deleted years ago -
    /// and it is not this plugin's business. Without that line the whole catalogue is a
    /// download queue: on a real server that was 1973 episodes, which is not a backlog, it
    /// is a bill. On the same server twelve of sixty-seven shows had no episode at all,
    /// and every page listed them.
    /// </para>
    ///
    /// <para>
    /// <b>It has to be still going out.</b> A series that ended is complete or it is not,
    /// and either way nothing new will appear - so searching for its gaps every five
    /// minutes forever buys nothing. The library is asked; this is not derived from air
    /// dates, which reads a series cancelled last month as current.
    /// </para>
    ///
    /// <para>
    /// A show named in <see cref="OrchestratorOptions.FollowedShowIds"/> is past both. That
    /// is the owner saying "this one anyway", and it is how a new show is started and how a
    /// finished one is filled in.
    /// </para>
    ///
    /// <para>
    /// The list is replaced, never merged, so a queue built under older rules is
    /// re-decided on the next tick instead of outliving the rules that made it.
    /// </para>
    /// </summary>
    public async Task<WantedRefresh> RefreshWantedAsync(CancellationToken ct)
    {
        List<WantedEpisode> missing = [];
        List<TrackedShow> tracked = [];
        HashSet<int> askedFor = [.. options.FollowedShowIds];
        int notOnTheServer = 0;
        int finished = 0;

        foreach (LibraryShow show in await library.GetShowsAsync(ct))
        {
            // A show with no folder cannot be a download target. Skipping it here beats
            // composing a path from null somewhere further down.
            if (show.Folder is null)
                continue;

            IReadOnlyList<LibraryEpisode> episodes = await library.GetEpisodesAsync(show.ShowId, ct);

            // Read from the episodes rather than the show's own have-count: both come from
            // the host, and the one this loop already trusts to decide "missing" is the one
            // that should decide "on the server", or the two can disagree. On a real server
            // the have-count is zero for shows with hundreds of episodes.
            bool onTheServer = episodes.Any(episode => episode.HasFile);
            bool asked = askedFor.Contains(show.ShowId);

            if (!asked)
            {
                if (!onTheServer)
                {
                    notOnTheServer++;
                    continue;
                }

                if (!show.Status.StillGoing())
                {
                    finished++;
                    continue;
                }
            }

            // Recorded whether or not anything is missing from it. A running series is up to
            // date most of the time - the week between one episode and the next - and a list
            // built from wanted episodes alone makes it vanish for exactly that week.
            tracked.Add(new TrackedShow
            {
                ShowId = show.ShowId,
                Title = show.Title,
                Started = onTheServer,
                Status = show.Status,
                NextAirDate = NextAirDate(episodes),
            });

            IReadOnlySet<int> unscheduled = UnscheduledSeasons(episodes);

            // By key, because the library can hand the same slot twice - one show reachable
            // through two libraries, or two rows for one episode. Left alone it reached the
            // store as two entries for one episode, which is what wedged a real server's
            // feed job. Deduping here means the store is never asked to hold it twice.
            HashSet<EpisodeKey> seen = [];

            foreach (LibraryEpisode episode in episodes)
            {
                if (episode.HasFile)
                    continue;

                if (episode.SeasonNumber == 0 && !options.IncludeSpecials)
                    continue;

                if (unscheduled.Contains(episode.SeasonNumber))
                    continue;

                if (!seen.Add(new EpisodeKey(show.ShowId, episode.SeasonNumber, episode.EpisodeNumber)))
                    continue;

                missing.Add(new WantedEpisode
                {
                    Key = new EpisodeKey(show.ShowId, episode.SeasonNumber, episode.EpisodeNumber),
                    ShowTitle = show.Title,
                    EpisodeTitle = episode.Title,
                    AirDate = episode.AirDate is DateTimeOffset aired ? DateOnly.FromDateTime(aired.UtcDateTime) : null,
                });
            }
        }

        await store.RefreshWantedAsync(missing, ct);

        // Recorded here rather than worked out again by whoever renders it. The page that
        // shows this list used to ask the library and read HaveEpisodeCount, which the
        // host reports as zero for shows that plainly have episodes - so it offered to
        // follow shows whose missing episodes were in the queue directly above it. This
        // loop already walked the episodes to decide; the decision is the thing to keep.
        await store.RecordShowsAsync(tracked, ct);

        return new WantedRefresh(missing.Count, tracked.Count, notOnTheServer, finished);
    }

    /// <summary>
    /// Seasons the library knows of but has not put a date on anywhere.
    ///
    /// <para>
    /// A metadata provider lists an announced season as placeholder rows - "Episode 1" to
    /// "Episode 8", no air dates - the moment it is ordered. Nobody can seed an episode
    /// that has not been made, so every one of those is searched for until it is parked as
    /// unavailable: on a real library that was eight episodes burning twelve rate-limited
    /// searches each, and then giving up shortly before the season actually arrived.
    /// </para>
    ///
    /// <para>
    /// The rule is off entirely for a library that dates nothing. Old libraries are full of
    /// episodes nobody ever dated, and undated only means "not scheduled" where a date is
    /// the norm - otherwise this would quietly abandon every episode of every show. One
    /// dated episode anywhere in the show is enough to turn it on.
    /// </para>
    /// </summary>
    private static IReadOnlySet<int> UnscheduledSeasons(IReadOnlyList<LibraryEpisode> episodes)
    {
        if (!episodes.Any(episode => episode.AirDate is not null))
            return new HashSet<int>();

        return new HashSet<int>(episodes
            .GroupBy(episode => episode.SeasonNumber)
            .Where(season => season.All(episode => episode.AirDate is null))
            .Select(season => season.Key));
    }

    /// <summary>When the next episode airs, when the library knows of one that has not yet.</summary>
    private static DateOnly? NextAirDate(IReadOnlyList<LibraryEpisode> episodes)
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.UtcNow);

        return episodes
            .Where(episode => episode.AirDate is not null)
            .Select(episode => DateOnly.FromDateTime(episode.AirDate!.Value.UtcDateTime))
            .Where(aired => aired >= today)
            .OrderBy(aired => aired)
            .Cast<DateOnly?>()
            .FirstOrDefault();
    }

    /// <summary>
    /// Searches for what is wanted and grabs what is worth grabbing.
    ///
    /// <para>
    /// Returns what it asked as well as what it took, because those are two different
    /// failures and they look identical from outside. A cycle that asked ten indexers and
    /// grabbed nothing is a profile turning things down or sites with nothing to offer; a
    /// cycle that asked nothing at all is a plugin that has quietly stopped working. The
    /// second one went unnoticed for a day.
    /// </para>
    /// </summary>
    public async Task<SearchCycle> SearchCycleAsync(CancellationToken ct)
    {
        int room = options.MaxConcurrentDownloads - await DownloadingNowAsync(ct);

        if (room <= 0)
            return new SearchCycle(0, 0);

        int grabbed = 0;
        int searched = 0;
        HashSet<EpisodeKey> settled = [];

        foreach (WantedEpisode episode in await SearchableAsync(ct))
        {
            if (grabbed >= room)
                break;

            // A pack taken earlier in this same cycle already settled this one. The batch
            // was read before that happened, so without this the plugin searches for an
            // episode it just grabbed and pays an indexer's rate limit to be told so.
            if (settled.Contains(episode.Key))
                continue;

            searched++;

            IReadOnlyList<EpisodeKey>? covered;

            try
            {
                covered = await TryGrabAsync(episode, ct);
            }
            catch (Exception failure) when (failure is not OperationCanceledException)
            {
                // One episode, one failure. This used to unwind the whole cycle: every
                // episode behind the bad one went unsearched, and because the throw came
                // out of the engine before AddGrabAsync, nothing anywhere recorded that a
                // release had been chosen and lost. Written down here so a page can say
                // what happened, and on to the next one.
                await store.RecordHistoryAsync(new HistoryEntry
                {
                    At = now(),
                    Event = HistoryEvent.Failed,
                    Key = episode.Key,
                    ShowTitle = episode.ShowTitle,
                    ReleaseTitle = episode.EpisodeTitle ?? Slot(episode.Key),
                    Detail = failure.Message,
                }, ct);

                continue;
            }

            if (covered is null)
                continue;

            settled.UnionWith(covered);
            grabbed++;
        }

        return new SearchCycle(searched, grabbed);
    }

    /// <summary>
    /// Whether a failure was simply that nobody had it.
    ///
    /// <para>
    /// Matched on the engine's own wording rather than a type, because the engine reports a
    /// reason and not a category - and inventing a category for this one case would put the
    /// judgement in the wrong place. The engine decides what went wrong; this decides
    /// whether it is worth telling anybody.
    /// </para>
    /// </summary>
    private static bool NobodyWasSeeding(string? reason) =>
        reason is not null && reason.Contains("no peer", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Every tracker worth announcing this torrent to, once each.
    ///
    /// <para>
    /// Order matters and is kept: what the indexers said comes first, because those are the
    /// swarms the release is actually in, and the owner's general ones follow. Compared
    /// case-insensitively so one tracker spelled two ways is not announced to twice.
    /// </para>
    /// </summary>
    private IReadOnlyList<string> Announce(IReadOnlyList<string> reported)
    {
        List<string> announce = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

        foreach (string tracker in reported.Concat(options.ExtraTrackers))
        {
            if (tracker.Length > 0 && seen.Add(tracker.Trim()))
                announce.Add(tracker.Trim());
        }

        return announce;
    }

    /// <summary>An episode named the way history reads best when no release got far enough to have a name.</summary>
    private static string Slot(EpisodeKey key) => $"S{key.Season:D2}E{key.Episode:D2}";

    /// <summary>
    /// How many downloads are actually running, which is not how many grabs are active.
    ///
    /// <para>
    /// A grab that has finished and is waiting on its move takes no bandwidth, no peer slot
    /// and no disk head. Counting it against the ceiling meant a download stuck on its
    /// import held one of the five places for as long as it stayed stuck - and two of them
    /// stayed stuck for a day. With one more downloading, that left room for a single new
    /// grab per cycle: a plugin that visibly worked through one show at a time, for a
    /// reason that had nothing to do with searching.
    /// </para>
    /// </summary>
    private async Task<int> DownloadingNowAsync(CancellationToken ct) =>
        (await store.ActiveGrabsAsync(ct)).Count(grab => grab.State != GrabState.Downloaded);

    /// <summary>
    /// Every episode worth asking an indexer about, least recently searched first.
    ///
    /// <para>
    /// Every one, across every show, in one cycle. It used to be ten per cycle, so a
    /// library behind on twenty shows was worked through a few episodes at a time and took
    /// hours to say anything about most of them - which reads, correctly, as a plugin
    /// doing one show at a time. Catching up is a burst and then nothing; capping the
    /// burst only makes the nothing arrive later. The indexer pacer is what keeps the
    /// asking polite, and it does that per indexer, which is where politeness is owed.
    /// </para>
    ///
    /// <para>
    /// Unaired episodes are dropped here rather than skipped inside the loop, and that
    /// distinction still matters. Nobody can seed an episode that has not aired, so
    /// searching for one spends an attempt on a question with no possible answer - twelve of
    /// those and the plugin parks it as unavailable, giving up shortly before it becomes the
    /// one thing worth looking for. So it is skipped without being marked, which is right,
    /// and which leaves it exactly where it was: at the head of a queue ordered by least
    /// recently searched.
    /// </para>
    ///
    /// <para>
    /// The whole list is read to do it. The store answers from memory, so the cost is one
    /// allocation - and it is the same read <see cref="FeedCycleAsync"/> and
    /// <see cref="GrabAsync"/> already make.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<WantedEpisode>> SearchableAsync(CancellationToken ct) =>
    [
        .. (await store.WantedAsync(int.MaxValue, ct))
            .Where(episode => !NotOutYet(episode)),
    ];

    /// <summary>
    /// Grabs from what the feed just posted, rather than from what was asked for.
    ///
    /// <para>
    /// One request gets the whole feed, and then the matching happens here rather than at
    /// the indexer: the feed does not know which episodes this server is missing, and
    /// asking it per episode would be the search cadence again at a fifteen-minute
    /// interval. Everything a release could plausibly answer for is offered to the same
    /// decider the search uses, so a feed grab is held to the owner's profile exactly as a
    /// searched one is - the feed decides what is <em>available</em>, never what is good
    /// enough.
    /// </para>
    ///
    /// <para>
    /// An episode the feed has nothing for is left untouched. It has not been searched
    /// for, and counting a quiet feed against its search attempts would park episodes as
    /// unavailable without anybody ever having asked an indexer about them.
    /// </para>
    /// </summary>
    public async Task<FeedCycle> FeedCycleAsync(CancellationToken ct)
    {
        if (feed is null)
            return new FeedCycle(0, 0);

        int room = options.MaxConcurrentDownloads - await DownloadingNowAsync(ct);

        if (room <= 0)
            return new FeedCycle(0, 0);

        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(int.MaxValue, ct);

        if (wanted.Count == 0)
            return new FeedCycle(0, 0);

        IReadOnlyList<ReleaseInfo> latest = await feed.LatestAsync(ct);

        List<(WantedEpisode Episode, List<ReleaseInfo> Candidates)> offers = [.. Offers(latest, wanted)];

        int grabbed = 0;
        HashSet<EpisodeKey> settled = [];

        foreach ((WantedEpisode episode, List<ReleaseInfo> candidates) in offers)
        {
            if (grabbed >= room)
                break;

            if (settled.Contains(episode.Key))
                continue;

            IReadOnlyList<EpisodeKey>? covered = await GrabAsync(episode, candidates, countsAsSearch: false, ct);

            if (covered is null)
                continue;

            settled.UnionWith(covered);
            grabbed++;
        }

        return new FeedCycle(offers.Count, grabbed);
    }

    /// <summary>
    /// Which wanted episodes each release could be, keyed by episode.
    ///
    /// <para>
    /// Matching is by show name and then by slot, and it is deliberately generous: a
    /// season pack is offered to every gap in that season, and a release whose name parses
    /// to nothing is offered to nobody. Being generous is safe because the decider filters
    /// on title and slot again before it picks anything - this only decides what is worth
    /// showing it, and a release shown to an episode it does not fit is simply refused.
    /// </para>
    ///
    /// <para>
    /// Shows are matched once each rather than once per wanted episode. A season of a show
    /// is twenty-odd episodes with the same name on it, and tokenizing that name twenty
    /// times per feed item is the difference between a cheap cadence and a hot one.
    /// </para>
    /// </summary>
    private static IEnumerable<(WantedEpisode Episode, List<ReleaseInfo> Candidates)> Offers(
        IReadOnlyList<ReleaseInfo> latest,
        IReadOnlyList<WantedEpisode> wanted)
    {
        Dictionary<int, List<WantedEpisode>> byShow = [];

        foreach (WantedEpisode episode in wanted)
        {
            if (!byShow.TryGetValue(episode.Key.ShowId, out List<WantedEpisode>? episodes))
                byShow[episode.Key.ShowId] = episodes = [];

            episodes.Add(episode);
        }

        Dictionary<EpisodeKey, (WantedEpisode Episode, List<ReleaseInfo> Candidates)> offers = [];

        foreach (ReleaseInfo release in latest)
        {
            EpisodeSlot? slot = ReleaseNameParser.ParseEpisode(release.Title);
            int? pack = ReleaseNameParser.ParseSeasonPack(release.Title);

            if (slot is null && pack is null)
                continue;

            foreach (List<WantedEpisode> episodes in byShow.Values)
            {
                if (!TitleMatcher.Matches(release.Title, episodes[0].ShowTitle))
                    continue;

                foreach (WantedEpisode episode in episodes)
                {
                    bool fits = slot is not null
                        ? slot.Value.Season == episode.Key.Season && slot.Value.Episode == episode.Key.Episode
                        : pack == episode.Key.Season;

                    if (!fits)
                        continue;

                    if (!offers.TryGetValue(episode.Key, out (WantedEpisode Episode, List<ReleaseInfo> Candidates) offer))
                        offers[episode.Key] = offer = (episode, []);

                    offer.Candidates.Add(release);
                }
            }
        }

        return offers.Values;
    }

    /// <summary>
    /// Whether this one is still in the future.
    ///
    /// <para>
    /// No air date means wanted. Unknown is not the same as future, and old libraries are
    /// full of episodes nobody ever dated - refusing to look for those would quietly
    /// abandon them.
    /// </para>
    /// </summary>
    private bool NotOutYet(WantedEpisode episode) =>
        episode.AirDate is DateOnly airs && airs > DateOnly.FromDateTime(now().UtcDateTime);

    /// <summary>The episodes the grab settled, or null when nothing was grabbed.</summary>
    private async Task<IReadOnlyList<EpisodeKey>?> TryGrabAsync(WantedEpisode episode, CancellationToken ct)
    {
        IReadOnlyList<ReleaseInfo> found = await search.SearchAsync(
            new SearchQuery(episode.ShowTitle, new EpisodeSlot(episode.Key.Season, episode.Key.Episode)),
            ct);

        return await GrabAsync(episode, found, countsAsSearch: true, ct);
    }

    /// <summary>
    /// Picks from a set of candidates and hands the winner to the engine, whether they
    /// came from a search or off a feed.
    ///
    /// <para>
    /// <paramref name="countsAsSearch"/> is the one difference between the two callers. A
    /// search that finds nothing is evidence about the episode and eventually parks it; a
    /// feed that happens not to mention it tonight is evidence about the feed.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyList<EpisodeKey>?> GrabAsync(
        WantedEpisode episode,
        IReadOnlyList<ReleaseInfo> found,
        bool countsAsSearch,
        CancellationToken ct)
    {
        // Everything still missing from this show and season, this episode included. It
        // decides both whether a pack is worth its bytes and, if one is taken, what the
        // grab is answering for.
        IReadOnlyList<EpisodeKey> gapsInSeason =
        [
            .. (await store.WantedAsync(int.MaxValue, ct))
                .Where(wanted => wanted.Key.ShowId == episode.Key.ShowId && wanted.Key.Season == episode.Key.Season)
                .Select(wanted => wanted.Key),
        ];

        List<ReleaseInfo> allowed = [];

        foreach (ReleaseInfo release in found)
        {
            if (!await store.IsBlacklistedAsync(release.InfoHash, release.Title, now(), ct))
                allowed.Add(release);
        }

        ReleaseInfo? chosen = chooser.Choose(episode, allowed, gapsInSeason.Count >= options.SeasonPackThreshold);

        if (chosen is null)
        {
            // Nothing usable. Park it once it has been asked for often enough that the
            // answer is not going to change on the next cycle either.
            WantedState next = episode.SearchAttempts + 1 >= options.MaxSearchAttempts
                ? WantedState.Unavailable
                : WantedState.Wanted;

            if (countsAsSearch)
                await store.MarkSearchedAsync(episode.Key, now(), next, ct);

            return null;
        }

        if (await NoRoomForAsync(chosen, episode, ct))
            return null;

        // An announcement carries no torrent, so the winner may be a name and nothing
        // more. Resolving it is a second question - not "which release" but "who has
        // this one" - and it is asked of the sites rather than of the feed that named it.
        if (chosen.MagnetUri is null && chosen.DownloadUrl is null && resolver is not null)
        {
            ReleaseInfo? resolved = await resolver.ResolveAsync(chosen, ct);

            if (resolved is not null)
            {
                // The announcement's identity, the resolved copy's whereabouts. Trackers
                // and seeders belong to whoever is actually serving it; the title stays
                // the announced one, because that is what the pack detection below and
                // the blacklist both key on.
                chosen = chosen with
                {
                    MagnetUri = resolved.MagnetUri,
                    DownloadUrl = resolved.DownloadUrl,
                    InfoHash = resolved.InfoHash ?? chosen.InfoHash,
                    Seeders = resolved.Seeders,
                    Trackers = resolved.Trackers.Count > 0 ? resolved.Trackers : chosen.Trackers,
                };
            }
        }

        string? source = chosen.MagnetUri ?? chosen.DownloadUrl;

        if (source is null)
        {
            // Announced and nobody has it - which happens, and is not the episode's fault.
            // Recorded so the page can say so, because otherwise this is indistinguishable
            // from a feed that never mentioned it.
            await store.RecordHistoryAsync(new HistoryEntry
            {
                At = now(),
                Event = HistoryEvent.Skipped,
                Key = episode.Key,
                ShowTitle = episode.ShowTitle,
                ReleaseTitle = chosen.Title,
                Indexer = chosen.IndexerName,
                Detail = "announced, but no site had it yet",
            }, ct);

            if (countsAsSearch)
                await store.MarkSearchedAsync(episode.Key, now(), WantedState.Wanted, ct);

            return null;
        }

        // A pack answers for every gap in the season it covers; an episode release
        // answers for itself. Claiming a gap the pack turns out not to contain is safe:
        // the wanted list is derived from the library on the next refresh, so an episode
        // that never arrived is simply wanted again.
        IReadOnlyList<EpisodeKey> covers =
            ReleaseNameParser.ParseSeasonPack(chosen.Title) == episode.Key.Season
                ? gapsInSeason
                : [episode.Key];

        // Everything anybody named for this torrent, plus the owner's own. The aggregator
        // has already merged what every indexer reported for this info hash; adding the
        // configured ones on top is what turns a torrent found through one site into a
        // torrent announced to every swarm the owner knows about.
        IReadOnlyList<string> trackers = Announce(chosen.Trackers);

        // Where this came from decides whether it may ever upload, and the same tracker
        // carries the targets that say when to stop. Both travel with the request: the
        // engine has no registry and is not going to grow one.
        TorrentOrigin origin = privateTrackers.OriginFor(chosen.Trackers);
        SwarmPolicy seeding = privateTrackers.PolicyFor(SwarmPolicy.Default, chosen.Trackers);

        string infoHash = await engine.AddAsync(new TorrentRequest
        {
            Source = source,
            DestinationFolder = options.DownloadFolder,
            Origin = origin,
            SeedRatioTarget = origin == TorrentOrigin.PrivateSeeding ? seeding.SeedRatioTarget : null,
            SeedTimeTarget = origin == TorrentOrigin.PrivateSeeding ? seeding.SeedTimeTarget : null,
            ExtraTrackers = trackers,
        }, ct);

        await store.AddGrabAsync(new Grab
        {
            InfoHash = infoHash,
            Key = episode.Key,
            Covers = covers,
            ReleaseTitle = chosen.Title,
            Source = source,
            Indexer = chosen.IndexerName,
            SizeBytes = chosen.SizeBytes,
            GrabbedAt = now(),
        }, ct);

        foreach (EpisodeKey key in covers)
            await store.MarkSearchedAsync(key, now(), WantedState.Grabbed, ct);

        // No history yet. Choosing something is not news: most of what is chosen off a
        // public tracker turns out to have nobody seeding it, and a line per choice buried
        // the two that actually arrived under a dozen that never started. The entry is
        // written when bytes appear - see TransfersCycleAsync.
        return covers;
    }

    /// <summary>
    /// Polls the engine and acts on what changed: what finished, what failed, and what the
    /// engine has forgotten since the last restart.
    /// </summary>
    public async Task<TransfersCycle> TransfersCycleAsync(CancellationToken ct)
    {
        int imported = 0;
        int putBack = 0;

        await ResumeForgottenAsync(ct);

        imported += await ImportFinishedAsync(ct);

        foreach (EngineTransfer transfer in await engine.TransfersAsync(ct))
        {
            Grab? grab = await store.FindGrabAsync(transfer.InfoHash, ct);

            if (grab is null)
                continue;

            await store.RecordTransferAsync(new Transfer
            {
                InfoHash = transfer.InfoHash,
                BytesDone = transfer.BytesDone,
                BytesTotal = transfer.BytesTotal,
                Peers = transfer.Peers,
                BytesPerSecond = transfer.BytesPerSecond,
                Paused = transfer.State == EngineState.Paused,
                UpdatedAt = now(),
            }, ct);

            switch (transfer.State)
            {
                // Only from Grabbed. A torrent that was already downloading and briefly
                // reports Resolving again must not be walked backwards.
                case EngineState.Resolving when grab.State == GrabState.Grabbed:
                    await store.UpdateGrabAsync(transfer.InfoHash, GrabState.Resolving, null, null, ct);
                    break;

                case EngineState.Downloading when grab.State is GrabState.Grabbed or GrabState.Resolving:
                    await store.UpdateGrabAsync(transfer.InfoHash, GrabState.Downloading, null, null, ct);

                    // Here rather than at the moment it was chosen, because this is the
                    // first point at which anything real has happened: a peer answered and
                    // bytes are moving. Once per download - the guard above only lets this
                    // arm run on the way out of Grabbed or Resolving.
                    await store.RecordHistoryAsync(new HistoryEntry
                    {
                        At = now(),
                        Event = HistoryEvent.Grabbed,
                        Key = grab.Key,
                        ShowTitle = null,
                        ReleaseTitle = grab.ReleaseTitle,
                        Indexer = grab.Indexer,
                        SizeBytes = grab.SizeBytes,
                        Detail = grab.Covered.Count > 1 ? $"a season pack covering {grab.Covered.Count} episodes" : null,
                    }, ct);

                    break;

                case EngineState.Completed when grab.State != GrabState.Imported:
                    if (transfer.CompletedFolder is string completed && await ImportAsync(grab, completed, ct))
                        imported++;

                    break;

                case EngineState.Failed:
                    putBack += await FailAsync(grab, transfer, ct);
                    break;
            }
        }

        return new TransfersCycle(imported, putBack);
    }

    /// <summary>
    /// Hands back to the engine any download it no longer knows about.
    ///
    /// <para>
    /// The engine keeps its torrents in memory, so a server restart empties it while the
    /// store still holds every grab. Nothing put them back, and the consequence was worse
    /// than a pause: a download that had already finished sat on disk marked Downloaded and
    /// was never asked about again, so the import never ran and no encode was ever queued.
    /// Three finished episodes were stranded that way on a real server.
    /// </para>
    ///
    /// <para>
    /// Here rather than at start-up because this cadence already runs every minute and
    /// already reconciles the engine against the store. A torrent the engine does know is
    /// left alone - AddAsync is idempotent, but asking it needlessly every minute is not
    /// free.
    /// </para>
    /// </summary>
    private async Task ResumeForgottenAsync(CancellationToken ct)
    {
        HashSet<string> known = [.. (await engine.TransfersAsync(ct)).Select(transfer => transfer.InfoHash)];

        foreach (Grab grab in await store.ActiveGrabsAsync(ct))
        {
            // A grab written before the source was kept cannot be resumed. Left alone
            // rather than guessed at: the wrong magnet downloads the wrong episode.
            if (known.Contains(grab.InfoHash) || grab.Source.Length == 0)
                continue;

            await engine.AddAsync(new TorrentRequest
            {
                Source = grab.Source,
                DestinationFolder = options.DownloadFolder,
                Origin = privateTrackers.OriginFor([]),
                ExtraTrackers = Announce([]),
            }, ct);
        }
    }

    /// <summary>
    /// Finishes any download whose bytes are already on disk, whether or not the engine
    /// still remembers it.
    ///
    /// <para>
    /// The import used to run only from an engine transfer, so a grab that reached
    /// Downloaded and then failed its move was retried only while the engine held the
    /// torrent. After a restart it was stranded - and if it was grabbed before the source
    /// was kept, <see cref="ResumeForgottenAsync"/> could not hand it back either, so
    /// nothing would ever look at it again. That is exactly what happened to two finished
    /// episodes: complete on disk, marked Downloaded, unreachable, and each holding one of
    /// the five concurrent download slots for as long as it sat there.
    /// </para>
    ///
    /// <para>
    /// A finished download does not need the engine. Every piece is in and verified; what
    /// is left is a file move, and the path to move is on the grab.
    /// </para>
    /// </summary>
    private async Task<int> ImportFinishedAsync(CancellationToken ct)
    {
        int imported = 0;

        foreach (Grab grab in await store.ActiveGrabsAsync(ct))
        {
            if (grab.State != GrabState.Downloaded || grab.CompletedPath.Length == 0)
                continue;

            if (await ImportAsync(grab, grab.CompletedPath, ct))
                imported++;
        }

        return imported;
    }

    private async Task<bool> ImportAsync(Grab grab, string completedPath, CancellationToken ct)
    {
        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Downloaded, null, null, ct);

        // Written down before anything is attempted, so a move that fails can be retried
        // from the store alone. See Grab.CompletedPath.
        if (grab.CompletedPath != completedPath)
            await store.RecordCompletedPathAsync(grab.InfoHash, completedPath, ct);

        // Let go of the files first. The engine keeps a FileStream open per file for as
        // long as it holds the torrent, and Windows refuses to rename a file that somebody
        // has open without share-delete - so the move threw, the mover swallowed it and
        // answered null, and the grab stayed Downloaded. Which is not a retry: the retry
        // only fires while the engine still reports the transfer, so after the next restart
        // it was stranded. Two finished episodes sat in the download folder for a day.
        //
        // Nothing is lost by letting go. Every piece is in and verified, and a public
        // torrent - which is all of them, unless the owner added a private tracker and
        // asked it to seed - has no business staying in the engine afterwards. If the move
        // still fails, the grab is left active and the next transfers cadence hands it back
        // to the engine, which finds the pieces on disk and completes again.
        await engine.RemoveAsync(grab.InfoHash, deleteFiles: false, ct);

        if (!await intake.MoveIntoIntakeAsync(completedPath, grab.Key, ct))
        {
            // The move did not happen, so the grab stays unfinished and the next cycle
            // tries again. An incomplete handoff is never recorded as a finished one.
            return false;
        }

        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Imported, null, now(), ct);

        foreach (EpisodeKey key in grab.Covered)
            await store.MarkSearchedAsync(key, now(), WantedState.Done, ct);

        // The one line that survives the download disappearing off the page. An episode
        // that is missing tomorrow morning is answered from here or from nowhere.
        await store.RecordHistoryAsync(new HistoryEntry
        {
            At = now(),
            Event = HistoryEvent.Imported,
            Key = grab.Key,
            ReleaseTitle = grab.ReleaseTitle,
            Indexer = grab.Indexer,
            SizeBytes = grab.SizeBytes,
            Detail = grab.Covered.Count > 1 ? $"{grab.Covered.Count} episodes" : null,
        }, ct);

        return true;
    }

    /// <summary>
    /// Searches for one episode right now, instead of when its turn comes round.
    ///
    /// <para>
    /// The search cadence works through a backlog ten at a time and least-recently-searched
    /// first, which is the right order for a machine and the wrong one for a person who
    /// wants tonight's episode. This is that person's button.
    /// </para>
    /// </summary>
    public async Task<bool> SearchNowAsync(EpisodeKey key, CancellationToken ct)
    {
        WantedEpisode? episode = await store.FindWantedAsync(key, ct);

        // Refused rather than searched, same as the cadence, and for the same reason -
        // but this one has somebody watching, so the caller turns it into a sentence.
        return episode is not null && !NotOutYet(episode) && await TryGrabAsync(episode, ct) is not null;
    }

    /// <summary>
    /// Takes a link the owner pasted, and files it under the episode it turns out to be.
    ///
    /// <para>
    /// Matched against the wanted list rather than taken on trust, and refused when it
    /// matches nothing. A torrent nobody can tie to an episode is one that downloads
    /// perfectly and then has no library to be imported into - it would sit complete in
    /// the finished folder for good. Refusing up front says so while the owner is still
    /// looking at the box they typed into.
    /// </para>
    ///
    /// <para>
    /// The name comes from the magnet's own <c>dn</c>. Whoever posted the torrent named it
    /// after the release, which is the same string every other part of this plugin reads.
    /// </para>
    /// </summary>
    public async Task<ManualAdd> AddManuallyAsync(string source, CancellationToken ct)
    {
        string trimmed = source.Trim();

        if (!trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase)
            && !Uri.TryCreate(trimmed, UriKind.Absolute, out _))
        {
            return new ManualAdd(false, "That is not a magnet link or a torrent URL.");
        }

        string? name = DisplayName(trimmed);

        if (name is null)
            return new ManualAdd(false, "That link does not say what it is. Use a magnet link with a name in it.");

        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(int.MaxValue, ct);

        ReleaseInfo release = new()
        {
            IndexerName = "added by hand",
            TorrentId = name,
            Title = name,
            MagnetUri = trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? trimmed : null,
            DownloadUrl = trimmed.StartsWith("magnet:", StringComparison.OrdinalIgnoreCase) ? null : trimmed,
        };

        (WantedEpisode Episode, List<ReleaseInfo> Candidates) match = Offers([release], wanted).FirstOrDefault();

        if (match.Episode is null)
            return new ManualAdd(false, $"Nothing on the queue matches {name}.");

        // Past the profile deliberately. The owner picked this one, and a plugin that
        // second-guesses a link somebody pasted by hand is a plugin they stop pasting into.
        string infoHash = await engine.AddAsync(new TorrentRequest
        {
            Source = trimmed,
            DestinationFolder = options.DownloadFolder,
            Origin = privateTrackers.OriginFor([]),
        }, ct);

        await store.AddGrabAsync(new Grab
        {
            InfoHash = infoHash,
            Key = match.Episode.Key,
            Covers = [match.Episode.Key],
            ReleaseTitle = name,
            Source = trimmed,
            Indexer = "added by hand",
            GrabbedAt = now(),
        }, ct);

        await store.MarkSearchedAsync(match.Episode.Key, now(), WantedState.Grabbed, ct);

        await store.RecordHistoryAsync(new HistoryEntry
        {
            At = now(),
            Event = HistoryEvent.Grabbed,
            Key = match.Episode.Key,
            ShowTitle = match.Episode.ShowTitle,
            ReleaseTitle = name,
            Indexer = "added by hand",
        }, ct);

        return new ManualAdd(true, $"Added for {match.Episode.ShowTitle}.");
    }

    /// <summary>The <c>dn</c> of a magnet, which is what the poster called the release.</summary>
    private static string? DisplayName(string source)
    {
        int start = source.IndexOf("dn=", StringComparison.OrdinalIgnoreCase);

        if (start < 0)
            return null;

        string rest = source[(start + 3)..];
        int end = rest.IndexOf('&');

        string name = Uri.UnescapeDataString(end < 0 ? rest : rest[..end]).Replace('+', ' ');

        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    /// <summary>
    /// Stops a download the owner wants stopped, keeping the bytes.
    ///
    /// <para>
    /// The grab stays exactly as it is. A paused torrent is still this plugin's answer to
    /// that episode, and putting the episode back on the wanted list would have the next
    /// search cycle grab a second copy of the thing the owner just stopped.
    /// </para>
    /// </summary>
    public async Task<bool> PauseDownloadAsync(string infoHash, CancellationToken ct)
    {
        if (await store.FindGrabAsync(infoHash, ct) is null)
            return false;

        await engine.PauseAsync(infoHash, ct);

        return true;
    }

    public async Task<bool> ResumeDownloadAsync(string infoHash, CancellationToken ct)
    {
        if (await store.FindGrabAsync(infoHash, ct) is null)
            return false;

        await engine.ResumeAsync(infoHash, ct);

        return true;
    }

    /// <summary>
    /// Throws a download away and looks for something else instead.
    ///
    /// <para>
    /// Blacklisted on the way out, which is the whole point of the button. Without it the
    /// next search cycle scores the same release top again and grabs it back within
    /// minutes, and the owner is left clicking cancel at a thing that will not go away.
    /// A cancel says "not this one", so the plugin has to remember which one.
    /// </para>
    ///
    /// <para>
    /// Every episode the grab covered goes back to wanted, not just the one it was filed
    /// under: they left the queue on the strength of this torrent, and it is gone.
    /// </para>
    /// </summary>
    public async Task<bool> CancelDownloadAsync(string infoHash, CancellationToken ct)
    {
        Grab? grab = await store.FindGrabAsync(infoHash, ct);

        if (grab is null)
            return false;

        await store.UpdateGrabAsync(infoHash, GrabState.Failed, "cancelled by the owner", now(), ct);

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = grab.InfoHash,
            ReleaseTitle = grab.ReleaseTitle,
            Reason = "cancelled by the owner",
            AddedAt = now(),
            ExpiresAt = now() + options.BlacklistDuration,
        }, ct);

        foreach (EpisodeKey key in grab.Covered)
            await store.MarkSearchedAsync(key, now(), WantedState.Wanted, ct);

        await engine.RemoveAsync(infoHash, deleteFiles: true, ct);

        await store.RecordHistoryAsync(new HistoryEntry
        {
            At = now(),
            Event = HistoryEvent.Cancelled,
            Key = grab.Key,
            ReleaseTitle = grab.ReleaseTitle,
            Indexer = grab.Indexer,
            SizeBytes = grab.SizeBytes,
            Detail = "cancelled by the owner, and skipped for now",
        }, ct);

        return true;
    }

    /// <summary>
    /// Whether taking this release would leave the disk too full, and says so where the
    /// owner will see it.
    ///
    /// <para>
    /// Refused rather than queued and failed later, because a torrent that runs the disk
    /// to zero takes the encoder, the databases and playback down with it - and none of
    /// those failures look like a download. The episode stays wanted and costs no search
    /// attempt: there is nothing wrong with it, and there will be room again once
    /// something finishes or somebody clears space.
    /// </para>
    /// </summary>
    private async Task<bool> NoRoomForAsync(ReleaseInfo chosen, WantedEpisode episode, CancellationToken ct)
    {
        long? free = freeSpace?.Invoke(options.DownloadFolder);

        if (free is null || chosen.SizeBytes <= 0 || free >= chosen.SizeBytes + options.MinimumFreeBytes)
            return false;

        await store.RecordHistoryAsync(new HistoryEntry
        {
            At = now(),
            Event = HistoryEvent.Skipped,
            Key = episode.Key,
            ShowTitle = episode.ShowTitle,
            ReleaseTitle = chosen.Title,
            Indexer = chosen.IndexerName,
            SizeBytes = chosen.SizeBytes,
            Detail = $"not enough free space: {Gigabytes(free.Value)} left, and this needs {Gigabytes(chosen.SizeBytes)}",
        }, ct);

        return true;
    }

    private static string Gigabytes(long bytes) => $"{bytes / (1024d * 1024 * 1024):0.#} GB";

    private async Task<int> FailAsync(Grab grab, EngineTransfer transfer, CancellationToken ct)
    {
        if (grab.State == GrabState.Failed)
            return 0;

        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Failed, transfer.FailureReason, now(), ct);

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = grab.InfoHash,
            ReleaseTitle = grab.ReleaseTitle,
            Reason = transfer.FailureReason ?? "the download failed",
            AddedAt = now(),
            ExpiresAt = now() + options.BlacklistDuration,
        }, ct);

        // Wanted again, so the next cycle looks for a different release. The blacklist
        // is what stops it choosing the same broken one. Every episode it covered, not
        // just the one that triggered it: the others left the queue on the strength of
        // this torrent, so its failure is theirs.
        foreach (EpisodeKey key in grab.Covered)
            await store.MarkSearchedAsync(key, now(), WantedState.Wanted, ct);

        await engine.RemoveAsync(grab.InfoHash, deleteFiles: true, ct);

        // A swarm with nobody in it is not an event. It is the ordinary weather of a public
        // tracker, the episode is already back on the queue, and there is nothing anybody
        // can do about it - so a line saying so is noise that hides the failures somebody
        // could act on. Everything else is written down.
        if (NobodyWasSeeding(transfer.FailureReason))
            return grab.Covered.Count;

        await store.RecordHistoryAsync(new HistoryEntry
        {
            At = now(),
            Event = HistoryEvent.Failed,
            Key = grab.Key,
            ReleaseTitle = grab.ReleaseTitle,
            Indexer = grab.Indexer,
            SizeBytes = grab.SizeBytes,
            Detail = transfer.FailureReason ?? "the download failed",
        }, ct);

        return grab.Covered.Count;
    }
}
