using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Reading every feed whole into the name pool.
/// </summary>
/// <remarks>
/// The feeds answer with pages really captured from them, so what is pooled
/// here is what the plugin would really pool.
/// </remarks>
public class HarvestTests
{
    /// <remarks>
    /// <strong>A2.</strong> A feed is read whole and never asked a question.
    /// 0.3.4 put SceneSource in the search set and made forty identical
    /// requests a cycle, each one answering with the same newest twenty posts —
    /// and every one of them succeeded, which is why nobody noticed.
    /// </remarks>
    [Fact]
    public async Task AFeedIsReadWholeAndNeverAskedAQuery()
    {
        FakeFetch fetch = new();
        fetch.Answers("https://predb.me/?rss=1", Capture.Fixture("predb.xml"));
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"));

        await Harvesting(fetch).RunAsync(CancellationToken.None);

        Assert.Equal(
            ["https://predb.me/?rss=1", "https://www.scnsrc.me/feed/"],
            fetch.Asked.Select(address => address.ToString()).Order());

        // PreDB has a search address as well, and it is never the one used
        // here. A question put to a feed answers with the newest posts whatever
        // was asked, which is indistinguishable from working.
        Assert.DoesNotContain(fetch.Asked, address => address.ToString().Contains("search", StringComparison.Ordinal));
    }

    /// <remarks>
    /// Every feed at once. Read one after another, a cycle costs the sum of the
    /// slowest sites; read together it costs the slowest one. The clock proves
    /// it: all of them are in flight before any of them is allowed to finish,
    /// which cannot happen unless they were started together.
    /// </remarks>
    [Fact]
    public async Task EveryFeedIsReadAtOnceRatherThanOneAfterAnother()
    {
        FakeTimeProvider clock = new();
        FakeFetch fetch = new(clock);
        fetch.Answers("https://predb.me/?rss=1", Capture.Fixture("predb.xml"), TimeSpan.FromSeconds(5));
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"), TimeSpan.FromSeconds(3));
        fetch.Answers("https://www.srrdb.com/feed/srrs", Capture.Fixture("srrdb.xml"), TimeSpan.FromSeconds(1));

        Task run = Harvesting(fetch, clock).RunAsync(CancellationToken.None);

        // Bounded, because a harvest that reads one feed at a time never gets
        // here at all and an unbounded wait would hang the suite rather than
        // fail it.
        await Task.WhenAny(fetch.AllInFlight, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            fetch.AllInFlight.IsCompletedSuccessfully,
            $"Only {fetch.InFlight} of {fetch.Expected} feeds were in flight, so they were read one at a time.");

        // One advance, the length of the slowest. If they were serial this
        // would leave two of them still waiting.
        clock.Advance(TimeSpan.FromSeconds(5));

        await run;
    }

    /// <remarks>
    /// The pool is keyed by the show and the slot, so a name found for an
    /// episode is found whichever feed carried it. Two feeds carrying the same
    /// release is the ordinary case — the same scene name is on all of them —
    /// and it is one name, not two.
    /// </remarks>
    [Fact]
    public async Task ThePoolIsKeyedByShowAndSlotAndDedupedByTitle()
    {
        FakeFetch fetch = new();

        // The same feed body from two sources: every name in it is a duplicate
        // of a name in the other.
        fetch.Answers("https://predb.me/?rss=1", Capture.Fixture("predb.xml"));
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("predb.xml"));

        FakePool pool = new();

        await Harvesting(fetch, pool: pool).RunAsync(CancellationToken.None);

        Assert.NotEmpty(pool.Names);

        // Silo S03E06 is the whole of the PreDB capture, in eight spellings.
        Assert.All(pool.Names, name => Assert.Equal("silo|s03e06", name.Key));

        Assert.Equal(
            pool.Names.Select(name => name.Title).Distinct(StringComparer.Ordinal).Count(),
            pool.Names.Count);
    }

    /// <remarks>
    /// A site being down is a site being down. 0.3.4's harvest ran each feed in
    /// turn inside one try block, so the first refusal ended the pass and every
    /// feed after it went unread — with nothing on the page to say so.
    /// </remarks>
    [Fact]
    public async Task OneFeedThatFailsDoesNotTakeTheHarvestDown()
    {
        FakeFetch fetch = new();
        fetch.Fails("https://predb.me/?rss=1", FetchOutcome.Unreachable, "predb.me did not answer");
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"));

        FakePool pool = new();
        ActivityJournal journal = new();

        int pooled = await Harvesting(fetch, pool: pool, journal: journal).RunAsync(CancellationToken.None);

        Assert.True(pooled > 0, "The feed that answered contributed nothing.");

        ActivitySnapshot snapshot = journal.Snapshot();

        Assert.Contains(
            snapshot.History,
            entry => entry.Outcome == ActivityOutcome.Failed
                     && entry.Subject == "PreDB"
                     && entry.Detail!.Contains("did not answer", StringComparison.Ordinal));

        // And nothing is left looking as though it were still running.
        Assert.Empty(snapshot.InFlight);
    }

    /// <remarks>
    /// And neither does one that goes wrong in a way nobody planned for. A
    /// reader is a regular expression over a page nobody here controls, so
    /// "this cannot throw" is not a thing that can be known about it — and the
    /// cost of being wrong is every feed after it going unread.
    /// </remarks>
    [Fact]
    public async Task AFeedThatThrowsDoesNotTakeTheHarvestDownEither()
    {
        FakeFetch fetch = new();
        fetch.Throws("https://predb.me/?rss=1", new InvalidOperationException("something nobody planned for"));
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"));

        FakePool pool = new();
        ActivityJournal journal = new();

        int pooled = await Harvesting(fetch, pool: pool, journal: journal).RunAsync(CancellationToken.None);

        Assert.True(pooled > 0, "The feed that answered contributed nothing.");

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed
                     && entry.Subject == "PreDB"
                     && entry.Detail!.Contains("nobody planned for", StringComparison.Ordinal));

        Assert.Empty(journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// A stage that cannot be seen does not ship. Every feed says when it
    /// started and how it ended, under its own name, or the dashboard shows a
    /// harvest that is either doing everything or nothing.
    /// </remarks>
    [Fact]
    public async Task EveryFeedSaysWhatItDidInTheJournal()
    {
        FakeFetch fetch = new();
        fetch.Answers("https://predb.me/?rss=1", Capture.Fixture("predb.xml"));
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"));

        ActivityJournal journal = new();

        await Harvesting(fetch, journal: journal).RunAsync(CancellationToken.None);

        ActivityEvent[] history = [.. journal.Snapshot().History.Where(entry => entry.Stage == ActivityStage.Harvest)];

        foreach (string feed in (string[])["PreDB", "SceneSource"])
        {
            Assert.Contains(history, entry => entry.Subject == feed && entry.Outcome == ActivityOutcome.Started);
            Assert.Contains(history, entry => entry.Subject == feed && entry.Outcome == ActivityOutcome.Finished);
        }

        Assert.Empty(journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// A name that answers for no episode is not a name this stage has any use
    /// for. The scene feeds are full of films, and a film keyed under nothing
    /// would sit in the pool for ever being compared against every episode.
    /// </remarks>
    [Fact]
    public async Task ANameWithNoSlotInItIsNotPooled()
    {
        FakeFetch fetch = new();
        fetch.Answers("https://www.scnsrc.me/feed/", Capture.Fixture("scenesource.xml"));

        FakePool pool = new();

        await Harvesting(fetch, pool: pool).RunAsync(CancellationToken.None);

        Assert.NotEmpty(pool.Names);
        Assert.DoesNotContain(pool.Names, name => name.Key.EndsWith('|'));

        // The capture really does carry films: this proves the assertion above
        // is refusing something rather than finding nothing to refuse.
        Assert.Contains(
            Capture.Rows("scenesource.xml", "rss"),
            title => title.Contains("Abrahams Boys", StringComparison.Ordinal));

        Assert.DoesNotContain(pool.Names, name => name.Title.Contains("Abrahams Boys", StringComparison.Ordinal));
    }

    /// <summary>The three feeds this file uses, as the catalogue really has them.</summary>
    private static readonly SourceDefinition[] Feeds =
    [
        new("PreDB", "rss", "https://predb.me/?rss=1")
        {
            SearchUrl = "https://predb.me/?search={query}&rss=1",
            SearchGated = true,
        },
        new("SceneSource", "rss", "https://www.scnsrc.me/feed/") { Gated = true },
        new("srrDB", "rss", "https://www.srrdb.com/feed/srrs"),
    ];

    private static Harvest Harvesting(
        FakeFetch fetch,
        TimeProvider? clock = null,
        FakePool? pool = null,
        ActivityJournal? journal = null)
    {
        return new(
            SourceCatalogue.Build(Feeds.Where(feed => fetch.Knows(feed.Url)), [], []),
            fetch,
            Readers.Shipped(),
            pool ?? new FakePool(),
            journal ?? new ActivityJournal(),
            clock ?? TimeProvider.System);
    }
}

/// <summary>The pool, in memory, so what was written can be looked at.</summary>
internal sealed class FakePool : INamePool
{
    private readonly Lock _lock = new();
    private readonly List<PooledName> _names = [];

    public IReadOnlyList<PooledName> Names
    {
        get
        {
            lock (_lock)
            {
                return [.. _names];
            }
        }
    }

    public Task AddAsync(IReadOnlyList<PooledName> names, CancellationToken ct)
    {
        lock (_lock)
        {
            _names.AddRange(names);
        }

        return Task.CompletedTask;
    }
}
