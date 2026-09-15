using Microsoft.Extensions.Time.Testing;

using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.NameSourceSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// The feeds of the four name sources, read at the start of a run and taken per episode.
/// </summary>
/// <remarks>
/// <c>docs/specs/release-names.md</c> § Reading the feeds and <c>run.md</c> § The order of a run. Every
/// feed answers with what it really carried on 15 September 2026.
/// </remarks>
public sealed class FeedNamesTests
{
    private const string KhloeShow = "The Girls: A Khloé Kardashian Project";

    private const string KhloeFive = "The.Girls.A.Khloe.Kardashian.Project.S01E05.720p.WEB.H264-AFO";

    /// <remarks>
    /// A feed release name is taken for an episode when its show and its season and episode number are
    /// those of an episode being searched for. The same name on three feeds is taken from each of them.
    /// Everything else the feeds carried is left: S01E04 of the same show is not searched for, S01E06
    /// has not aired, and Below Deck Mediterranean is a show nobody switched on, so none of them is in
    /// the list the run hands over.
    /// </remarks>
    [Fact]
    public async Task AFeedNameIsTakenForTheEpisodeItsShowAndNumberName()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds();

        TrackedEpisode khloeFive = Episode(1, KhloeShow, 1, 5, year: 2026);
        TrackedEpisode khloeSix = Episode(1, KhloeShow, 1, 6, EpisodeState.NotAired, year: 2026);
        TrackedEpisode allAmerican = Episode(2, "All American", 8, 11, year: 2018);

        FeedNamesTaken taken = await Over(fetch).ReadFeedsAsync([khloeFive, khloeSix, allAmerican], CancellationToken.None);

        Assert.Equal([KhloeFive], taken.For(khloeFive.Key).Select(name => name.Title).Distinct());
        Assert.Equal(["PreDB", "PreDB.net", "srrDB"], taken.For(khloeFive.Key).Select(name => name.Source).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["All American S08E11 1080p WEB H264-GGWP"],
            taken.For(allAmerican.Key).Select(name => name.Title));

        Assert.Empty(taken.For(khloeSix.Key));

        Assert.Equal(4, taken.All.Count());

        // The feeds really carried what was left, so the test is about leaving it and not about its
        // absence.
        Assert.Contains(Capture.Rows("names-predb-feed.xml", "rss"), title => title.Contains("Project.S01E04", StringComparison.Ordinal));
        Assert.Contains(Capture.Rows("names-predb-feed.xml", "rss"), title => title.Contains("Project.S01E06", StringComparison.Ordinal));
        Assert.Contains(Capture.Rows("names-scenesource-feed.xml", "rss"), title => title.StartsWith("Below Deck Mediterranean S11E15", StringComparison.Ordinal));
    }

    /// <remarks>
    /// A release name without an episode number, such as a season pack, is not searched for — so it is
    /// not taken, even for a season whose every episode is being searched for.
    /// </remarks>
    [Fact]
    public async Task ANameWithNoEpisodeNumberIsNeverTaken()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds();

        FeedNamesTaken taken = await Over(fetch).ReadFeedsAsync(
            [Episode(3, "Forever Home", 1, 1), Episode(3, "Forever Home", 1, 2)],
            CancellationToken.None);

        Assert.Empty(taken.All);
        Assert.Contains("Forever Home S01 1080p MY5 WEB-DL AAC2.0 H.264-TBN", Capture.Rows("names-scenesource-feed.xml", "rss"));
    }

    /// <remarks>
    /// <strong>A2.</strong> A feed is read at its own address and never asked a question. 0.3.4 put
    /// SceneSource in the search set and made forty identical requests a cycle, each one answering with
    /// the same newest posts — and every one of them succeeded, which is why nobody noticed.
    /// </remarks>
    [Fact]
    public async Task AFeedIsReadWholeAndNeverAskedAQuery()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds();

        await Over(fetch).ReadFeedsAsync([Episode(2, "All American", 8, 11)], CancellationToken.None);

        Assert.Equal(Feeds.Order(StringComparer.Ordinal), fetch.Asked.Select(address => address.ToString()).Order(StringComparer.Ordinal));
    }

    /// <remarks>
    /// <c>run.md</c>: a run reads the feeds of all name sources at the same time. Read one after another
    /// a run costs the sum of the slowest sites. The clock proves it: all four are in flight before any
    /// of them is allowed to finish, which cannot happen unless they were started together.
    /// </remarks>
    [Fact]
    public async Task EveryFeedIsReadAtOnceRatherThanOneAfterAnother()
    {
        FakeTimeProvider clock = new();
        FakeFetch fetch = new FakeFetch(clock).AnsweringFeeds(TimeSpan.FromSeconds(5));

        Task<FeedNamesTaken> run = Over(fetch, clock: clock).ReadFeedsAsync([Episode(2, "All American", 8, 11)], CancellationToken.None);

        // Bounded, because feeds read one at a time never get here and an unbounded wait would hang the
        // suite rather than fail it.
        await Task.WhenAny(fetch.AllInFlight, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            fetch.AllInFlight.IsCompletedSuccessfully,
            $"Only {fetch.InFlight} of {fetch.Expected} feeds were in flight, so they were read one at a time.");

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.NotEmpty((await run).All);
    }

    /// <remarks>
    /// A stage that cannot be seen does not ship. Every feed says when it started and how it ended,
    /// under its own name.
    /// </remarks>
    [Fact]
    public async Task EveryFeedSaysWhatItDidInTheJournal()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds();
        ActivityJournal journal = new();

        await Over(fetch, journal).ReadFeedsAsync([Episode(2, "All American", 8, 11)], CancellationToken.None);

        ActivityEvent[] history = [.. journal.Snapshot().History.Where(entry => entry.Stage == ActivityStage.Harvest)];

        foreach (string feed in (string[])["PreDB", "srrDB", "PreDB.net", "SceneSource"])
        {
            Assert.Contains(history, entry => entry.Subject == feed && entry.Outcome == ActivityOutcome.Started);
            Assert.Contains(history, entry => entry.Subject == feed && entry.Outcome == ActivityOutcome.Finished);
        }

        Assert.Empty(journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// A feed is a source like any other, and the Sources page says what every source last answered.
    /// </remarks>
    [Fact]
    public async Task EveryFeedReadIsWrittenDownWithWhatItAnswered()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().Fails(SceneSourceFeed, FetchOutcome.Unreachable, "no such host");
        RecordingLedger ledger = new();

        await Over(fetch, ledger: ledger).ReadFeedsAsync([Episode(2, "All American", 8, 11)], CancellationToken.None);

        SourceAnswer answered = ledger.Answers.Single(one => one.Name == "PreDB");

        Assert.True(answered.Rows > 0, "The captured feed is full of release names.");
        Assert.Null(answered.Refusal);

        SourceAnswer unreachable = ledger.Answers.Single(one => one.Name == "SceneSource");

        Assert.Equal(0, unreachable.Rows);
        Assert.Contains("no such host", unreachable.Refusal!, StringComparison.Ordinal);
    }
}
