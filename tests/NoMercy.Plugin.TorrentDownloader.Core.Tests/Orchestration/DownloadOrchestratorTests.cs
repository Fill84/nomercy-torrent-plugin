// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;

public class DownloadOrchestratorTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private readonly InMemoryDownloadStore _store = new();
    private readonly FakeLibrary _library = new();
    private readonly FakeSearch _search = new();
    private readonly FakeEngine _engine = new();
    private readonly FakeIntake _intake = new();
    private readonly FakeChooser _chooser = new();
    private readonly FakeFeed _feed = new();
    private readonly FakeResolver _resolver = new();

    private DownloadOrchestrator Orchestrator(OrchestratorOptions? options = null) => new(
        _library,
        _store,
        _search,
        _chooser,
        _engine,
        _intake,
        options ?? new OrchestratorOptions { DownloadFolder = "/downloads" },
        new PrivateTrackerRegistry([]),
        () => Now,
        _feed,
        _ => FreeBytes,
        _resolver);

    /// <summary>What the disk claims to have left. Enough for anything, unless a test says otherwise.</summary>
    private long? FreeBytes { get; set; } = 500L * 1024 * 1024 * 1024;

    private static ReleaseInfo Release(string title = "Some.Show.S01E01.1080p.WEB-DL", string? hash = "abc123") => new()
    {
        IndexerName = "site-a",
        TorrentId = "1",
        Title = title,
        InfoHash = hash,
        MagnetUri = $"magnet:?xt=urn:btih:{hash ?? "0000000000000000000000000000000000000000"}",
        SizeBytes = 2_000_000_000,
        Seeders = 40,
        Trackers = ["udp://tracker.test:1337/announce"],
    };

    // --- refresh -----------------------------------------------------------------

    [Fact]
    public async Task RefreshWantedAsync_WantsEveryEpisodeWithoutAFile()
    {
        _library.Add(showId: 1, "Some Show", folder: "/media/some-show", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
            (1, 3, HasFile: false),
        ]);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(2);
        (await _store.WantedAsync(10, CancellationToken.None)).Should().HaveCount(2);
    }

    /// <summary>
    /// The gap that made a running series disappear.
    ///
    /// <para>
    /// Only wanted episodes and unstarted shows were recorded, so a show with a file for
    /// every episode the library knows about existed in no list this plugin held. A weekly
    /// series is in exactly that state for six days out of seven - up to date, with one more
    /// coming - and it was invisible on every page until it fell behind.
    /// </para>
    /// </summary>
    [Fact]
    public async Task RefreshWantedAsync_RecordsAShowThatIsUpToDate()
    {
        _library.Add(showId: 1, "Silo", folder: "/media/silo", episodes: [(1, 1, HasFile: true)]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        TrackedShow show = (await _store.ShowsAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        show.Title.Should().Be("Silo");
        show.Started.Should().BeTrue();
    }

    // --- only what is on the server, and only while it is still going out ---------

    // The library is a catalogue, not a shelf. A show it lists with no episode on the
    // server is one the metadata provider knows about - a row left behind by an "add
    // content" nobody followed through on, or a folder deleted years ago. On a real
    // server twelve of sixty-seven were in exactly that state, and every page listed them.
    [Fact]
    public async Task RefreshWantedAsync_RecordsNothingAtAllForAShowWithNoEpisodeOnTheServer()
    {
        _library.Add(showId: 1, "Never Watched", folder: "/media/never", episodes:
        [
            (1, 1, HasFile: false),
            (1, 2, HasFile: false),
        ]);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(0);
        refresh.Shows.Should().Be(0);
        refresh.NotOnTheServer.Should().Be(1);
        (await _store.ShowsAsync(CancellationToken.None)).Should().BeEmpty("a show nobody has is not this plugin's business");
    }

    // Half an episode is still an episode: one file anywhere in the show is what makes it
    // the owner's rather than the catalogue's.
    [Fact]
    public async Task RefreshWantedAsync_HoldsAShowWithASingleEpisodeOnTheServer()
    {
        _library.Add(showId: 1, "Barely Started", folder: "/media/barely", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ], status: ShowStatus.Returning);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Shows.Should().Be(1);
        refresh.Wanted.Should().Be(1);
    }

    [Theory]
    [InlineData(ShowStatus.Ended)]
    [InlineData(ShowStatus.Canceled)]
    public async Task RefreshWantedAsync_LeavesAFinishedShowAloneEvenWithGaps(ShowStatus finished)
    {
        _library.Add(showId: 1, "Finished", folder: "/media/finished", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ], status: finished);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(0);
        refresh.Shows.Should().Be(0);
        refresh.Finished.Should().Be(1);
        (await _store.ShowsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    [Theory]
    [InlineData(ShowStatus.Returning)]
    [InlineData(ShowStatus.Planned)]
    [InlineData(ShowStatus.InProduction)]
    [InlineData(ShowStatus.Pilot)]
    public async Task RefreshWantedAsync_WorksOnAnythingThatIsNotFinished(ShowStatus going)
    {
        _library.Add(showId: 1, "Going", folder: "/media/going", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ], status: going);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(1);
    }

    // A server too old to answer the question reports Unknown for every show. Reading that
    // as finished would stop the plugin working on an entire library the moment somebody
    // upgraded the plugin without upgrading the server.
    [Fact]
    public async Task RefreshWantedAsync_TreatsAnUnknownStatusAsStillGoing()
    {
        _library.Add(showId: 1, "No Status", folder: "/media/unknown", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ], status: ShowStatus.Unknown);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(1);
    }

    // The owner's override, and the only way past either rule. Asking for a finished show
    // is how a back catalogue gets filled in, and it is a decision the plugin has no
    // business second-guessing.
    [Fact]
    public async Task RefreshWantedAsync_WorksOnAFinishedShowThatWasAskedForByHand()
    {
        _library.Add(showId: 7, "Long Over", folder: "/media/over", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ], status: ShowStatus.Ended);

        WantedRefresh refresh = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            FollowedShowIds = [7],
        }).RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
        refresh.Shows.Should().Be(1);
        refresh.Finished.Should().Be(0, "it was asked for, so it is not being left alone");
    }

    [Fact]
    public async Task RefreshWantedAsync_KeepsTheStatusSoAPageCanSayWhichItIs()
    {
        _library.Add(showId: 1, "Airing", folder: "/media/airing", episodes: [(1, 1, HasFile: true)],
            status: ShowStatus.Returning);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        TrackedShow show = (await _store.ShowsAsync(CancellationToken.None)).Should().ContainSingle().Subject;

        show.Status.Should().Be(ShowStatus.Returning);
        show.Running.Should().BeTrue();
    }

    // A scheduled episode is what the page puts next to a show that is up to date today.
    [Fact]
    public async Task RefreshWantedAsync_RecordsWhenTheNextEpisodeAirs()
    {
        _library.Add(showId: 1, "On A Break", folder: "/media/break", episodes:
        [
            (1, 1, HasFile: true),
            (2, 1, HasFile: false),
        ], status: ShowStatus.Returning);
        _library.SetAirDate(1, 1, 1, Now.AddYears(-2));
        _library.SetAirDate(1, 2, 1, Now.AddDays(40));

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        TrackedShow show = (await _store.ShowsAsync(CancellationToken.None)).Single();

        show.NextAirDate.Should().Be(DateOnly.FromDateTime(Now.AddDays(40).UtcDateTime));
    }

    // --- a season that has not been scheduled -------------------------------------

    // Seen on a real library: an announced season two carried eight rows called
    // "Episode 1".."Episode 8" with no air date on any of them. Nobody can seed an episode
    // that has not been made, so every one of those was searched for until it was parked
    // as unavailable - twelve wasted searches each, against sites that rate limit.
    [Fact]
    public async Task RefreshWantedAsync_DoesNotWantASeasonNothingHasBeenScheduledIn()
    {
        _library.Add(showId: 1, "Announced", folder: "/media/announced", episodes:
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: true),
            (2, 1, HasFile: false),
            (2, 2, HasFile: false),
        ], status: ShowStatus.Returning);

        _library.SetAirDate(1, 1, 1, Now.AddYears(-1));
        _library.SetAirDate(1, 1, 2, Now.AddYears(-1).AddDays(7));

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(0);
        refresh.Shows.Should().Be(1, "the show itself is still held - only the unscheduled season is passed over");
    }

    // The moment one episode of it has a date, the season is real and the rest of it is
    // wanted: a weekly season lists every slot as soon as the first one airs.
    [Fact]
    public async Task RefreshWantedAsync_WantsASeasonAsSoonAsAnythingInItIsScheduled()
    {
        _library.Add(showId: 1, "Started Airing", folder: "/media/started", episodes:
        [
            (1, 1, HasFile: true),
            (2, 1, HasFile: false),
            (2, 2, HasFile: false),
        ], status: ShowStatus.Returning);

        _library.SetAirDate(1, 1, 1, Now.AddYears(-1));
        _library.SetAirDate(1, 2, 1, Now.AddDays(-2));

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(2);
    }

    // Old libraries are full of episodes nobody ever dated. Undated is only evidence of
    // "not scheduled" when the library dates anything at all - otherwise this rule would
    // quietly abandon every episode of every show.
    [Fact]
    public async Task RefreshWantedAsync_StillWantsAnUndatedSeasonWhenTheLibraryDatesNothing()
    {
        _library.Add(showId: 1, "Undated Everywhere", folder: "/media/undated", episodes:
        [
            (1, 1, HasFile: true),
            (2, 1, HasFile: false),
            (2, 2, HasFile: false),
        ], status: ShowStatus.Returning);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(2);
    }

    [Fact]
    public async Task RefreshWantedAsync_SkipsAShowWithNoFolderToDownloadInto()
    {
        _library.Add(showId: 1, "Homeless Show", folder: null, episodes: [(1, 1, false)]);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(0);
    }

    // The counterpart of the shelf rule: without this the plugin can only ever finish a
    // show somebody already started by hand, and can never begin one.
    [Fact]
    public async Task RefreshWantedAsync_FollowsAShowWithNothingOnTheServerWhenItWasAskedTo()
    {
        _library.Add(showId: 7, "Asked For", folder: "/media/asked", episodes: [(1, 1, false), (1, 2, false)]);

        WantedRefresh refresh = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            FollowedShowIds = [7],
        }).RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(2);
        refresh.Shows.Should().Be(1);
        refresh.NotOnTheServer.Should().Be(0, "it was asked for, so it is not being left alone");
    }

    [Fact]
    public async Task RefreshWantedAsync_StillLeavesAloneAShowThatWasNotAskedFor()
    {
        _library.Add(showId: 7, "Asked For", folder: "/media/asked", episodes: [(1, 1, false)]);
        _library.Add(showId: 8, "Not Asked For", folder: "/media/not", episodes: [(1, 1, false)]);

        WantedRefresh refresh = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            FollowedShowIds = [7],
        }).RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
        refresh.NotOnTheServer.Should().Be(1);
        (await _store.ShowsAsync(CancellationToken.None)).Should().ContainSingle()
            .Which.ShowId.Should().Be(7);
    }

    // Skipping quietly is how an owner concludes the plugin is broken. The counts are what
    // the log line is built from, and each one answers a different "why is it doing
    // nothing": nobody has it, or nobody is making any more of it.
    [Fact]
    public async Task RefreshWantedAsync_CountsWhatItPassedOverAndWhy()
    {
        _library.Add(showId: 1, "Held", folder: "/media/held", episodes: [(1, 1, true), (1, 2, false)],
            status: ShowStatus.Returning);
        _library.Add(showId: 2, "Never Watched", folder: "/media/never", episodes: [(1, 1, false)]);
        _library.Add(showId: 3, "Also Never", folder: "/media/also", episodes: [(1, 1, false)]);
        _library.Add(showId: 4, "Finished", folder: "/media/finished", episodes: [(1, 1, true), (1, 2, false)],
            status: ShowStatus.Ended);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
        refresh.Shows.Should().Be(1);
        refresh.NotOnTheServer.Should().Be(2);
        refresh.Finished.Should().Be(1);
    }

    [Fact]
    public async Task RefreshWantedAsync_LeavesSpecialsOutOfTheQueue()
    {
        _library.Add(showId: 1, "Some Show", folder: "/media/some-show", episodes:
        [
            (1, 1, HasFile: true),
            (0, 1, HasFile: false),
            (1, 2, HasFile: false),
        ]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        IReadOnlyList<WantedEpisode> wanted = await _store.WantedAsync(10, CancellationToken.None);
        wanted.Should().ContainSingle().Which.Key.Should().Be(new EpisodeKey(1, 1, 2));
    }

    [Fact]
    public async Task RefreshWantedAsync_TakesSpecialsWhenTheyAreTurnedOn()
    {
        _library.Add(showId: 1, "Some Show", folder: "/media/some-show", episodes:
        [
            (1, 1, HasFile: true),
            (0, 1, HasFile: false),
        ]);

        WantedRefresh refresh = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            IncludeSpecials = true,
        }).RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
    }

    // The library handed the same episode twice on a real server - one show listed in two
    // places, or two rows for one slot - and it went into the store twice, which is what
    // wedged the feed job. Deduping here means the store is never asked to hold a set
    // twice over in the first place.
    [Fact]
    public async Task RefreshWantedAsync_WantsAnEpisodeOnceEvenWhenTheLibraryListsItTwice()
    {
        _library.Add(showId: 1, "Doubled Show", folder: "/media/doubled", episodes:
        [
            (1, 1, HasFile: true),
            (2, 18, HasFile: false),
            (2, 18, HasFile: false),
        ]);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle();
    }

    // A queue built under the old rules is not left standing: the refresh replaces the
    // list wholesale, so entries the new rules no longer want disappear on the next tick
    // rather than needing anyone to clear anything by hand.
    [Fact]
    public async Task RefreshWantedAsync_DropsWhatAnEarlierRunWantedAndTheRulesNoLongerDo()
    {
        await _store.RefreshWantedAsync(
            [
                new WantedEpisode { Key = new EpisodeKey(2, 0, 1), ShowTitle = "Never Watched" },
                new WantedEpisode { Key = new EpisodeKey(1, 0, 1), ShowTitle = "Started" },
                new WantedEpisode { Key = new EpisodeKey(1, 1, 2), ShowTitle = "Started" },
            ],
            CancellationToken.None);

        _library.Add(showId: 1, "Started", folder: "/media/started", episodes: [(1, 1, true), (0, 1, false), (1, 2, false)]);
        _library.Add(showId: 2, "Never Watched", folder: "/media/never", episodes: [(0, 1, false)]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        IReadOnlyList<WantedEpisode> wanted = await _store.WantedAsync(10, CancellationToken.None);
        wanted.Should().ContainSingle().Which.Key.Should().Be(new EpisodeKey(1, 1, 2));
    }

    // --- a download that has not started yet ---------------------------------------------

    /// <summary>
    /// A magnet is registered before anybody has said what it contains, so there is a real
    /// span with a grab, no bytes and no size. Recorded as its own state rather than left
    /// at Grabbed, because the page reads the grab to decide what to draw.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_MarksAGrabThatIsStillLookingForPeers()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        string hash = _engine.Added.Should().ContainSingle().Subject.Source;
        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Resolving }];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        (await _store.FindGrabAsync(hash, CancellationToken.None))!
            .State.Should().Be(GrabState.Resolving);
    }

    // Forwards only. A torrent already downloading that briefly reports Resolving again
    // must not be walked backwards into a state the page draws as "not started".
    [Fact]
    public async Task TransfersCycleAsync_DoesNotWalkADownloadingTorrentBackToResolving()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        string hash = _engine.Added.Should().ContainSingle().Subject.Source;

        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Downloading }];
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Resolving }];
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        (await _store.FindGrabAsync(hash, CancellationToken.None))!
            .State.Should().Be(GrabState.Downloading);
    }

    // The bytes start arriving after the swarm answers, and the grab has to follow.
    [Fact]
    public async Task TransfersCycleAsync_MovesFromResolvingToDownloadingWhenTheBytesStart()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        string hash = _engine.Added.Should().ContainSingle().Subject.Source;

        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Resolving }];
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Downloading }];
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        (await _store.FindGrabAsync(hash, CancellationToken.None))!
            .State.Should().Be(GrabState.Downloading);
    }

    /// <summary>
    /// A download waiting on its swarm is still a download.
    ///
    /// <para>
    /// It was left out of the active set when Resolving was added, and the consequence was
    /// not cosmetic: the cycle works out how much room it has from that set, so five
    /// resolving downloads counted as zero and the next cycle started five more. On a real
    /// server that filled the page with torrents nobody had asked for, five minutes apart,
    /// with no ceiling at all.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_ADownloadStillFindingPeersCountsAgainstTheLimit()
    {
        _library.Add(showId: 1, "Some Show", "/media/some-show",
            [(1, 1, true), .. Enumerable.Range(2, 9).Select(episode => (1, episode, false))],
            status: ShowStatus.Returning);

        foreach (int episode in Enumerable.Range(2, 9))
            _library.SetAirDate(1, 1, episode, Now.AddDays(-30));

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        _search.UniquePerQuery = true;
        _search.Results = [Release()];

        DownloadOrchestrator orchestrator = Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 2,
        });

        await orchestrator.SearchCycleAsync(CancellationToken.None);

        // Everything taken so far is still resolving, which is where it sits for as long as
        // the swarm takes to answer.
        foreach (Grab grab in await _store.ActiveGrabsAsync(CancellationToken.None))
            await _store.UpdateGrabAsync(grab.InfoHash, GrabState.Resolving, null, null, CancellationToken.None);

        await orchestrator.SearchCycleAsync(CancellationToken.None);

        _engine.Added.Should().HaveCount(2, "two at a time means two, whatever state they are waiting in");
    }

    // The page reads the grab to find out what a transfer is called. Left out of the active
    // set, a resolving download showed the owner a raw info hash and nothing else.
    [Fact]
    public async Task ActiveGrabs_IncludeTheOnesStillFindingPeers()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        string hash = _engine.Added.Should().ContainSingle().Subject.Source;
        await _store.UpdateGrabAsync(hash, GrabState.Resolving, null, null, CancellationToken.None);

        (await _store.ActiveGrabsAsync(CancellationToken.None))
            .Should().ContainSingle().Which.ReleaseTitle.Should().NotBeNullOrEmpty();
    }

    // --- one failure costs one episode -------------------------------------------------

    /// <summary>
    /// A cadence that stops at the first bad episode never reaches the good ones, and on a
    /// real server the first one was bad every single cycle - for a fortnight.
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_AnEpisodeThatThrowsDoesNotStopTheOnesBehindIt()
    {
        _library.Add(showId: 1, "First", "/media/first", [(1, 1, true), (1, 2, false)],
            status: ShowStatus.Returning);
        _library.Add(showId: 2, "Second", "/media/second", [(1, 1, true), (1, 2, false)],
            status: ShowStatus.Returning);
        _library.SetAirDate(1, 1, 2, Now.AddDays(-2));
        _library.SetAirDate(2, 1, 2, Now.AddDays(-2));

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        _search.UniquePerQuery = true;
        _search.Results = [Release()];
        _engine.ThrowOnceWith = new InvalidOperationException("the swarm hung up");

        SearchCycle cycle = await Orchestrator().SearchCycleAsync(CancellationToken.None);

        cycle.Searched.Should().Be(2, "the second episode is still owed a search");
        cycle.Grabbed.Should().Be(1);
    }

    // Without this the failure is as invisible as it was before: the throw happened before
    // AddGrabAsync, so nothing anywhere remembered that a release had been chosen.
    [Fact]
    public async Task SearchCycleAsync_SaysOnThePageWhyAnEpisodeCouldNotBeStarted()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        _engine.ThrowOnceWith = new InvalidOperationException("the swarm hung up");

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
        entry.Event.Should().Be(HistoryEvent.Failed);
        entry.Detail.Should().Contain("the swarm hung up");
    }

    // Cancellation is the server shutting down, not the episode failing. Recording it as a
    // failure would fill history with noise every restart.
    [Fact]
    public async Task SearchCycleAsync_DoesNotRecordAShutdownAsAnEpisodeFailure()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        _engine.ThrowOnceWith = new OperationCanceledException();

        Func<Task> cycle = () => Orchestrator().SearchCycleAsync(CancellationToken.None);

        await cycle.Should().ThrowAsync<OperationCanceledException>();
        _store.History.Should().BeEmpty();
    }

    // --- announced here, downloaded from there -----------------------------------------

    private static ReleaseInfo Announcement(string title) => new()
    {
        IndexerName = "scene-feed",
        TorrentId = title,
        Title = title,

        // The whole point of an announcement: it says what exists, not where it is.
        MagnetUri = null,
        DownloadUrl = null,
    };

    // Without this the plugin matches an episode perfectly and has nothing to hand the
    // engine - which from outside looks exactly like a feed that found nothing at all.
    [Fact]
    public async Task SearchCycleAsync_ResolvesAnAnnouncementAgainstTheSitesAndGrabsWhatTheyHave()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Announcement("Some.Show.S01E01.1080p.WEB-DL-GROUP")];
        _resolver.Answer = Release("Some.Show.S01E01.1080p.WEB-DL-GROUP", "fromsite");

        int grabbed = (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(1);
        _resolver.Asked.Should().ContainSingle().Which.Should().Be("Some.Show.S01E01.1080p.WEB-DL-GROUP");
        _engine.Added.Should().ContainSingle();
    }

    // A release that already carries its own magnet is not an announcement, and asking the
    // sites about it would be a search per grab for nothing.
    [Fact]
    public async Task SearchCycleAsync_DoesNotResolveWhatAlreadyHasAMagnet()
    {
        await GrabOneAsync();

        _resolver.Asked.Should().BeEmpty();
    }

    // Announced and nobody has it yet is a real outcome, and not the episode's fault.
    [Fact]
    public async Task SearchCycleAsync_SaysOnThePageWhenNothingCouldBeResolved()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Announcement("Some.Show.S01E01.1080p.WEB-DL-GROUP")];
        _resolver.Answer = null;

        (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);

        HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
        entry.Event.Should().Be(HistoryEvent.Skipped);
        entry.Detail.Should().Contain("no site had it");
    }

    // --- episodes that have not happened yet -------------------------------------------

    // Seen on a real server: the queue was offering South Park S29E01, which has not
    // aired. Nobody can seed it. Searching for it spends an attempt per cycle on a
    // question with no possible answer, and after twelve of those the plugin parks it as
    // unavailable - giving up on it shortly before it becomes the one thing worth looking
    // for.
    [Fact]
    public async Task RefreshWantedAsync_KeepsAnUnairedEpisodeOnTheQueueButNeverSearchesForIt()
    {
        _library.Add(showId: 1, "Some Show", "/media/some-show",
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ]);
        _library.SetAirDate(1, 1, 2, Now.AddDays(9));

        // Still on the queue - it is what the owner is waiting for - but never searched
        // for, because nobody can seed it yet.
        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(1);

        (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);
        _search.Queries.Should().BeEmpty();
        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.SearchAttempts.Should().Be(0, "an unaired episode is skipped, not spent");
    }

    /// <summary>
    /// The starvation this cadence sat in on a real server: nothing was searched for a day
    /// and nothing said so.
    ///
    /// <para>
    /// The batch is the least-recently-searched episodes, and an unaired one is skipped
    /// without being marked - correctly, because nobody can seed it and spending an attempt
    /// on it would park it as unavailable shortly before it airs. But that leaves it at the
    /// head of the queue forever. Nineteen unaired episodes against a batch of ten meant the
    /// same ten were fetched, skipped and refetched every five minutes, and the twenty-three
    /// episodes that could have been searched were never reached.
    /// </para>
    ///
    /// <para>
    /// So the batch has to be that many <em>searchable</em> episodes, not that many rows of
    /// which some happen to be searchable.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_ReachesPastAWholeBatchOfEpisodesThatHaveNotAiredYet()
    {
        _library.Add(showId: 1, "Not Out Yet", "/media/waiting",
            [(1, 1, true), .. Enumerable.Range(2, 12).Select(episode => (1, episode, false))]);

        // Every one of them still to come, and more of them than one batch holds.
        foreach (int episode in Enumerable.Range(2, 12))
            _library.SetAirDate(1, 1, episode, Now.AddDays(30 + episode));

        // Sorts after all of those - a higher show id, and nothing has been searched yet -
        // so it is only reached by a cycle that does not spend its batch on the unaired.
        _library.Add(showId: 2, "Out Now", "/media/out", [(1, 1, true), (1, 2, false)]);
        _library.SetAirDate(2, 1, 2, Now.AddDays(-3));

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);
        _search.Results = [Release("Out.Now.S01E02.1080p.WEB-DL")];

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        SearchQuery asked = _search.Queries.Should().ContainSingle(
            "the batch is ten searchable episodes, not ten rows").Subject;

        asked.ShowName.Should().Be("Out Now");
        asked.Slot.Should().Be(new EpisodeSlot(1, 2));
    }

    // Skipping an unaired episode must still not cost it an attempt, or twelve quiet cycles
    // park it as unavailable shortly before it becomes the one thing worth looking for.
    [Fact]
    public async Task SearchCycleAsync_StillSpendsNoAttemptOnTheOnesItReachedPast()
    {
        _library.Add(showId: 1, "Not Out Yet", "/media/waiting", [(1, 1, true), (1, 2, false)]);
        _library.SetAirDate(1, 1, 2, Now.AddDays(40));

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.SearchAttempts.Should().Be(0);
    }

    [Fact]
    public async Task SearchNowAsync_RefusesAnEpisodeThatHasNotAiredYet()
    {
        _library.Add(showId: 1, "Some Show", "/media/some-show", [(1, 1, true), (1, 2, false)]);
        _library.SetAirDate(1, 1, 2, Now.AddDays(9));
        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        (await Orchestrator().SearchNowAsync(new EpisodeKey(1, 1, 2), CancellationToken.None)).Should().BeFalse();
        _search.Queries.Should().BeEmpty();
    }

    [Fact]
    public async Task RefreshWantedAsync_WantsAnEpisodeTheDayItAirs()
    {
        _library.Add(showId: 1, "Some Show", "/media/some-show",
        [
            (1, 1, HasFile: true),
            (1, 2, HasFile: false),
        ]);
        _library.SetAirDate(1, 1, 2, Now.AddHours(-1));

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(1);
    }

    // Unknown is not the same as future, and old libraries are full of undated episodes.
    [Fact]
    public async Task RefreshWantedAsync_StillWantsAnEpisodeWithNoAirDateAtAll()
    {
        _library.Add(showId: 1, "Some Show", "/media/some-show", [(1, 1, true), (1, 2, false)]);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(1);
    }

    // --- the owner overruling it -------------------------------------------------------

    [Fact]
    public async Task SearchNowAsync_GoesAndGetsOneEpisodeWithoutWaitingForItsTurn()
    {
        await WantEpisodesAsync(20);
        _search.Results = [Release("Some.Show.S01E19.1080p.WEB-DL", "now")];

        (await Orchestrator().SearchNowAsync(new EpisodeKey(1, 1, 19), CancellationToken.None)).Should().BeTrue();

        _search.Queries.Should().ContainSingle("only the one asked for, not the batch");
        _engine.Added.Should().ContainSingle();
    }

    [Fact]
    public async Task SearchNowAsync_SaysNoForAnEpisodeThatIsNotWanted()
    {
        (await Orchestrator().SearchNowAsync(new EpisodeKey(1, 1, 1), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task AddManuallyAsync_TakesALinkAndFilesItUnderTheEpisodeItMatches()
    {
        await WantEpisodesAsync(3);

        ManualAdd added = await Orchestrator().AddManuallyAsync(
            "magnet:?xt=urn:btih:deadbeef&dn=Some.Show.S01E02.1080p.WEB-DL-GROUP",
            CancellationToken.None);

        added.Added.Should().BeTrue();
        (await _store.ActiveGrabsAsync(CancellationToken.None)).Should().ContainSingle()
            .Which.Key.Should().Be(new EpisodeKey(1, 1, 2));
    }

    // A torrent nobody can tie to an episode downloads perfectly and then has no library
    // to be imported into. Refusing while the owner is still looking at the box beats a
    // complete file sitting in the finished folder for good.
    [Fact]
    public async Task AddManuallyAsync_RefusesALinkThatMatchesNothingOnTheQueue()
    {
        await WantEpisodesAsync(3);

        ManualAdd added = await Orchestrator().AddManuallyAsync(
            "magnet:?xt=urn:btih:deadbeef&dn=Some.Other.Show.S09E09.1080p",
            CancellationToken.None);

        added.Added.Should().BeFalse();
        added.Message.Should().Contain("Nothing on the queue matches");
        _engine.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task AddManuallyAsync_RefusesALinkWithNoNameInIt()
    {
        await WantEpisodesAsync(3);

        ManualAdd added = await Orchestrator().AddManuallyAsync(
            "magnet:?xt=urn:btih:deadbeef",
            CancellationToken.None);

        added.Added.Should().BeFalse();
        _engine.Added.Should().BeEmpty();
    }

    // The owner picked this one. A plugin that second-guesses a link somebody pasted by
    // hand is a plugin they stop pasting into.
    [Fact]
    public async Task AddManuallyAsync_TakesTheLinkEvenWhenTheProfileWouldHaveRefusedIt()
    {
        await WantEpisodesAsync(3);
        _chooser.Accept = false;

        ManualAdd added = await Orchestrator().AddManuallyAsync(
            "magnet:?xt=urn:btih:deadbeef&dn=Some.Show.S01E02.2160p.REMUX",
            CancellationToken.None);

        added.Added.Should().BeTrue();
    }

    // --- what is being skipped, and taking it back -------------------------------------

    // The list was invisible before. A release skipped for a fortnight is the likeliest
    // reason an episode keeps not arriving, and an owner who cannot see the list cannot
    // tell that from "nobody is seeding it" - two problems with different answers.
    [Fact]
    public async Task CancelDownloadAsync_ShowsUpInWhatIsBeingSkipped()
    {
        await GrabOneAsync();
        Grab grab = (await _store.ActiveGrabsAsync(CancellationToken.None))[0];

        await Orchestrator().CancelDownloadAsync(grab.InfoHash, CancellationToken.None);

        (await _store.BlacklistedAsync(Now, CancellationToken.None)).Should().ContainSingle()
            .Which.ReleaseTitle.Should().Be(grab.ReleaseTitle);
    }

    [Fact]
    public async Task AllowAgainAsync_LetsARefusedReleaseBeChosenOnceMore()
    {
        await GrabOneAsync();
        Grab grab = (await _store.ActiveGrabsAsync(CancellationToken.None))[0];
        await Orchestrator().CancelDownloadAsync(grab.InfoHash, CancellationToken.None);

        BlacklistEntry skipped = (await _store.BlacklistedAsync(Now, CancellationToken.None))[0];

        (await _store.AllowAgainAsync(skipped.Handle, CancellationToken.None)).Should().BeTrue();

        (await _store.IsBlacklistedAsync(grab.InfoHash, grab.ReleaseTitle, Now, CancellationToken.None))
            .Should().BeFalse();
    }

    // The handle is derived, not stored, so it has to come out the same on every render -
    // otherwise the button on the page names an entry that no longer answers to it.
    [Fact]
    public async Task Handle_IsTheSameEveryTimeForTheSameEntry()
    {
        BlacklistEntry entry = new() { InfoHash = "abc123", ReleaseTitle = "Some.Release", Reason = "why", AddedAt = Now };

        entry.Handle.Should().Be((entry with { }).Handle);
        entry.Handle.Should().NotBe(new BlacklistEntry { InfoHash = "def456", Reason = "why", AddedAt = Now }.Handle);
    }

    // --- room on the disk --------------------------------------------------------------

    // A media server that fills its own disk stops encoding, stops writing databases and
    // stops playing back, all at once and for reasons that look nothing like a torrent.
    [Fact]
    public async Task SearchCycleAsync_WillNotTakeAReleaseThatWouldFillTheDisk()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        FreeBytes = 3L * 1024 * 1024 * 1024;

        int grabbed = (await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MinimumFreeBytes = 20L * 1024 * 1024 * 1024,
        }).SearchCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(0);
        _engine.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchCycleAsync_SaysOnThePageWhyItSkippedOne()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        FreeBytes = 3L * 1024 * 1024 * 1024;

        await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MinimumFreeBytes = 20L * 1024 * 1024 * 1024,
        }).SearchCycleAsync(CancellationToken.None);

        HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
        entry.Event.Should().Be(HistoryEvent.Skipped);
        entry.Detail.Should().Contain("free space");
    }

    // Nothing is wrong with the episode, and there will be room again when something
    // finishes. Charging it a search attempt would eventually park it as unavailable.
    [Fact]
    public async Task SearchCycleAsync_AnEpisodeSkippedForSpaceKeepsItsSearchAttempts()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        FreeBytes = 3L * 1024 * 1024 * 1024;

        await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MinimumFreeBytes = 20L * 1024 * 1024 * 1024,
        }).SearchCycleAsync(CancellationToken.None);

        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.SearchAttempts.Should().Be(0);
    }

    // A share that will not report its size is not a reason to stop downloading.
    [Fact]
    public async Task SearchCycleAsync_StillDownloadsWhenTheDiskWillNotSayHowFullItIs()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        FreeBytes = null;

        (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(1);
    }

    // --- history ---------------------------------------------------------------------

    // The page only ever showed the present. This is the line that answers "what happened
    // to that episode" the morning after it left the list.
    [Fact]
    public async Task SearchCycleAsync_WritesDownWhatItGrabbedAndWhereFrom()
    {
        await GrabOneAsync();

        HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
        entry.Event.Should().Be(HistoryEvent.Grabbed);
        entry.ShowTitle.Should().Be("Some Show");
        entry.Indexer.Should().Be("site-a");
    }

    [Fact]
    public async Task TransfersCycleAsync_WritesDownAnImport()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [Completed(hash, "/downloads/Some.Show.S01E01")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _store.History.Should().Contain(entry => entry.Event == HistoryEvent.Imported);
    }

    // Failed on its own sends a reader to the log file this page exists to save them from.
    [Fact]
    public async Task TransfersCycleAsync_WritesDownWhyAFailureFailed()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [Failed(hash, "no peers after 30 minutes")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        HistoryEntry entry = _store.History.Should().ContainSingle(entry => entry.Event == HistoryEvent.Failed).Subject;
        entry.Detail.Should().Be("no peers after 30 minutes");
    }

    // --- the owner's own hands ------------------------------------------------------

    [Fact]
    public async Task PauseDownloadAsync_StopsTheTorrentAndLeavesTheEpisodeSettled()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;

        (await Orchestrator().PauseDownloadAsync(hash, CancellationToken.None)).Should().BeTrue();

        _engine.Paused.Should().ContainSingle().Which.Should().Be(hash);

        // Not back on the queue: a paused torrent is still the answer to that episode, and
        // re-wanting it would have the next search cycle grab a second copy.
        (await _store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task PauseDownloadAsync_SaysNoToAHashItIsNotHolding()
    {
        (await Orchestrator().PauseDownloadAsync("nothing", CancellationToken.None)).Should().BeFalse();
        _engine.Paused.Should().BeEmpty();
    }

    [Fact]
    public async Task CancelDownloadAsync_DeletesTheFilesAndPutsTheEpisodeBack()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;

        (await Orchestrator().CancelDownloadAsync(hash, CancellationToken.None)).Should().BeTrue();

        _engine.Removed.Should().ContainSingle().Which.Should().Be(hash);
        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle();
    }

    // Without this the next search cycle scores the same release top again and grabs it
    // straight back, and cancel becomes a button that does nothing you can see.
    [Fact]
    public async Task CancelDownloadAsync_BlacklistsTheReleaseSoItIsNotGrabbedStraightBack()
    {
        await GrabOneAsync();
        Grab grab = (await _store.ActiveGrabsAsync(CancellationToken.None))[0];

        await Orchestrator().CancelDownloadAsync(grab.InfoHash, CancellationToken.None);

        (await _store.IsBlacklistedAsync(grab.InfoHash, grab.ReleaseTitle, Now, CancellationToken.None))
            .Should().BeTrue();
    }

    [Fact]
    public async Task CancelDownloadAsync_PutsBackEveryEpisodeAPackWasCovering()
    {
        string hash = await GrabAPackAsync(4);

        await Orchestrator().CancelDownloadAsync(hash, CancellationToken.None);

        (await _store.WantedAsync(10, CancellationToken.None)).Should().HaveCount(4,
            "all four left the queue on the strength of that one torrent");
    }

    // --- feed ----------------------------------------------------------------------

    // The point of having a feed at all: nobody asked for this episode by name, it was
    // simply posted, and the plugin noticed within the quarter of an hour.
    [Fact]
    public async Task FeedCycleAsync_GrabsAWantedEpisodeThatShowedUpOnTheFeed()
    {
        await WantEpisodesAsync(2);
        _feed.Latest = [Release("Some.Show.S01E02.1080p.WEB-DL", "feedhash")];

        int grabbed = (await Orchestrator().FeedCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(1);
        _search.Queries.Should().BeEmpty("a feed is read, not asked");
        (await _store.ActiveGrabsAsync(CancellationToken.None)).Should().ContainSingle()
            .Which.Key.Should().Be(new EpisodeKey(1, 1, 2));
    }

    [Fact]
    public async Task FeedCycleAsync_IgnoresReleasesForShowsAndEpisodesNobodyIsMissing()
    {
        await WantEpisodesAsync(1);
        _feed.Latest =
        [
            Release("Some.Other.Show.S01E01.1080p.WEB-DL", "wrongshow"),
            Release("Some.Show.S04E09.1080p.WEB-DL", "wrongslot"),
            Release("Some.Show.Behind.The.Scenes.1080p.WEB-DL", "noslot"),
        ];

        (await Orchestrator().FeedCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);
        (await _store.ActiveGrabsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    // A quiet feed is not evidence about the episode. Counting it as a failed search
    // would park episodes as unavailable that no indexer was ever asked about.
    [Fact]
    public async Task FeedCycleAsync_DoesNotSpendAnEpisodesSearchAttempts()
    {
        await WantEpisodesAsync(1);
        _feed.Latest = [Release("Some.Show.S01E01.1080p.WEB-DL", "rejected")];
        _chooser.Accept = false;

        await Orchestrator().FeedCycleAsync(CancellationToken.None);

        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.SearchAttempts.Should().Be(0);
    }

    // The feed says what exists; the profile still says what is good enough. Offering the
    // release to the same decider the search uses is what keeps those two separate.
    [Fact]
    public async Task FeedCycleAsync_StillRefusesWhatTheProfileWouldRefuse()
    {
        await WantEpisodesAsync(1);
        _feed.Latest = [Release("Some.Show.S01E01.2160p.WEB-DL", "toobig")];
        _chooser.Accept = false;

        (await Orchestrator().FeedCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);
        _chooser.LastCandidates.Should().ContainSingle("it was offered, and turned down");
    }

    [Fact]
    public async Task FeedCycleAsync_ASeasonPackOnTheFeedSettlesEveryEpisodeItCovers()
    {
        await WantEpisodesAsync(3);
        _feed.Latest = [Release("Some.Show.S01.1080p.WEB-DL", "feedpack")];

        int grabbed = (await Orchestrator().FeedCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(1, "one torrent, not one per episode");
        (await _store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task FeedCycleAsync_StopsAtTheConcurrentDownloadLimit()
    {
        await WantEpisodesAsync(4);
        _feed.Latest =
        [
            Release("Some.Show.S01E01.1080p.WEB-DL", "one"),
            Release("Some.Show.S01E02.1080p.WEB-DL", "two"),
            Release("Some.Show.S01E03.1080p.WEB-DL", "three"),
        ];

        int grabbed = (await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 2,
        }).FeedCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(2);
    }

    // --- season packs ------------------------------------------------------------

    [Fact]
    public async Task SearchCycleAsync_ASeasonPackIsOneGrabThatSettlesEveryEpisodeItCovers()
    {
        await WantEpisodesAsync(4);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];

        int grabbed = (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(1, "one torrent was added, not one per episode");
        (await _store.ActiveGrabsAsync(CancellationToken.None)).Should().ContainSingle()
            .Which.Covers.Should().HaveCount(4);

        // The whole point: none of the four is still asking to be searched for. Before
        // this, a pack grabbed for episode 1 left the other three wanted, and they were
        // grabbed again as three more torrents of the same bytes.
        (await _store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();
    }

    [Fact]
    public async Task SearchCycleAsync_DoesNotKeepSearchingForEpisodesThePackJustCovered()
    {
        await WantEpisodesAsync(4);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        _search.Queries.Should().ContainSingle("the other three were settled before their turn came round");
    }

    [Fact]
    public async Task TransfersCycleAsync_APackThatImportsMarksEveryEpisodeItCoveredDone()
    {
        string hash = await GrabAPackAsync(4);
        _engine.Transfers = [Completed(hash, "/downloads/pack")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        for (int number = 1; number <= 4; number++)
        {
            WantedEpisode? episode = await _store.FindWantedAsync(new EpisodeKey(1, 1, number), CancellationToken.None);
            episode!.State.Should().Be(WantedState.Done, $"episode {number} was inside the pack");
        }
    }

    [Fact]
    public async Task TransfersCycleAsync_APackThatFailsPutsEveryEpisodeItCoveredBackInTheQueue()
    {
        string hash = await GrabAPackAsync(4);
        _engine.Transfers = [Failed(hash, "no peers")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        // All four, not just the one that triggered the grab: the other three were taken
        // off the queue on the strength of this torrent, so its failure is their failure.
        (await _store.WantedAsync(10, CancellationToken.None)).Should().HaveCount(4);
    }

    // A pack costs a whole season of bytes. Spending that to settle a single gap is the
    // kind of arithmetic nobody checks until the disk fills.
    [Fact]
    public async Task SearchCycleAsync_WillNotConsiderAPackForASeasonMissingOnlyOneEpisode()
    {
        await WantEpisodesAsync(1);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        _chooser.LastAllowedSeasonPacks.Should().BeFalse();
    }

    [Fact]
    public async Task SearchCycleAsync_ConsidersAPackOnceEnoughOfTheSeasonIsMissing()
    {
        await WantEpisodesAsync(3);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        _chooser.LastAllowedSeasonPacks.Should().BeTrue();
    }

    [Fact]
    public async Task SearchCycleAsync_ASingleEpisodeGrabCoversOnlyItself()
    {
        await WantEpisodesAsync(3);
        _search.Results = [Release()];

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        Grab grab = (await _store.ActiveGrabsAsync(CancellationToken.None)).First();
        grab.Covers.Should().Equal(grab.Key);
    }

    /// <summary>Grabs one pack and hands back the info hash the engine gave it.</summary>
    private async Task<string> GrabAPackAsync(int episodes)
    {
        await WantEpisodesAsync(episodes);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        return _engine.Added[0].Source;
    }

    // --- search and grab ---------------------------------------------------------

    [Fact]
    public async Task SearchCycleAsync_GrabsTheChosenReleaseAndRecordsIt()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];

        int grabbed = (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed;

        grabbed.Should().Be(1);
        _engine.Added.Should().ContainSingle();
        _engine.Added[0].DestinationFolder.Should().Be("/downloads");
        _engine.Added[0].ExtraTrackers.Should().Equal("udp://tracker.test:1337/announce");

        Grab? grab = await _store.FindGrabAsync(_engine.Added[0].Source, CancellationToken.None);
        grab.Should().NotBeNull();
        grab!.ReleaseTitle.Should().Be("Some.Show.S01E01.1080p.WEB-DL");
        grab.Indexer.Should().Be("site-a");
    }

    [Fact]
    public async Task SearchCycleAsync_LeavesTheEpisodeWantedWhenNothingIsGoodEnough()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        _chooser.Accept = false;

        (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);

        WantedEpisode? episode = await _store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None);
        episode!.State.Should().Be(WantedState.Wanted);
        episode.SearchAttempts.Should().Be(1);
    }

    [Fact]
    public async Task SearchCycleAsync_ParksAnEpisodeNobodyIsSeeding()
    {
        await WantOneEpisodeAsync();
        _chooser.Accept = false;

        DownloadOrchestrator orchestrator = Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxSearchAttempts = 2,
        });

        await orchestrator.SearchCycleAsync(CancellationToken.None);
        await orchestrator.SearchCycleAsync(CancellationToken.None);

        // Asking forever for something that is not out there is how a plugin spends its
        // whole cycle on the same handful of rows.
        WantedEpisode? episode = await _store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None);
        episode!.State.Should().Be(WantedState.Unavailable);
    }

    [Fact]
    public async Task SearchCycleAsync_NeverChoosesABlacklistedRelease()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];

        await _store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = "abc123",
            Reason = "failed last time",
            AddedAt = Now,
            ExpiresAt = Now.AddDays(14),
        }, CancellationToken.None);

        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        _chooser.LastCandidates.Should().BeEmpty();
        _engine.Added.Should().BeEmpty();
    }

    [Fact]
    public async Task SearchCycleAsync_StopsAtTheConcurrentDownloadCeiling()
    {
        await WantEpisodesAsync(20);
        _search.Results = [Release()];
        _search.UniquePerQuery = true;

        int grabbed = (await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 3,
            SearchBatchSize = 20,
        }).SearchCycleAsync(CancellationToken.None)).Grabbed;

        // The bound that turns a first run on a library with years of gaps into a steady
        // stream rather than two hundred downloads fighting over one connection.
        grabbed.Should().Be(3);
    }

    [Fact]
    public async Task SearchCycleAsync_DoesNothingWhileTheCeilingIsAlreadyReached()
    {
        await WantEpisodesAsync(5);
        _search.Results = [Release()];
        _search.UniquePerQuery = true;

        DownloadOrchestrator orchestrator = Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 2,
        });

        await orchestrator.SearchCycleAsync(CancellationToken.None);
        _engine.Added.Clear();

        (await orchestrator.SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(0);
        _engine.Added.Should().BeEmpty();
    }

    // --- transfers ---------------------------------------------------------------

    [Fact]
    public async Task TransfersCycleAsync_MirrorsProgressSoTheUiCanBeDrawn()
    {
        await GrabOneAsync();
        _engine.Transfers = [Downloading(_engine.Added[0].Source, done: 500, total: 1000, peers: 12)];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        IReadOnlyList<Transfer> transfers = await _store.TransfersAsync(CancellationToken.None);
        transfers.Should().ContainSingle();
        transfers[0].Progress.Should().Be(0.5);
        transfers[0].Peers.Should().Be(12);
    }

    [Fact]
    public async Task TransfersCycleAsync_HandsAFinishedDownloadToTheIntake()
    {
        await GrabOneAsync();
        string hash = _engine.Added[0].Source;
        _engine.Transfers = [Completed(hash, "/downloads/some.show.s01e01")];

        int imported = await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        imported.Should().Be(1);
        _intake.Moved.Should().ContainSingle().Which.Folder.Should().Be("/downloads/some.show.s01e01");

        (await _store.FindGrabAsync(hash, CancellationToken.None))!.State.Should().Be(GrabState.Imported);
        (await _store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None))!.State.Should().Be(WantedState.Done);
    }

    [Fact]
    public async Task TransfersCycleAsync_DoesNotCallItImportedWhenTheMoveFailed()
    {
        await GrabOneAsync();
        string hash = _engine.Added[0].Source;
        _engine.Transfers = [Completed(hash, "/downloads/some.show.s01e01")];
        _intake.Succeed = false;

        (await Orchestrator().TransfersCycleAsync(CancellationToken.None)).Should().Be(0);

        // An incomplete handoff is never recorded as a finished one - the same invariant
        // the engine keeps for pieces, one layer up.
        (await _store.FindGrabAsync(hash, CancellationToken.None))!.State.Should().Be(GrabState.Downloaded);
        (await _store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None))!.State.Should().NotBe(WantedState.Done);
    }

    [Fact]
    public async Task TransfersCycleAsync_RetriesAFailedMoveOnTheNextCycle()
    {
        await GrabOneAsync();
        _engine.Transfers = [Completed(_engine.Added[0].Source, "/downloads/some.show.s01e01")];
        _intake.Succeed = false;

        DownloadOrchestrator orchestrator = Orchestrator();
        await orchestrator.TransfersCycleAsync(CancellationToken.None);

        _intake.Succeed = true;
        (await orchestrator.TransfersCycleAsync(CancellationToken.None)).Should().Be(1);
    }

    [Fact]
    public async Task TransfersCycleAsync_BlacklistsAFailedReleaseAndWantsTheEpisodeAgain()
    {
        await GrabOneAsync();
        string hash = _engine.Added[0].Source;
        _engine.Transfers = [Failed(hash, "no peers after 30 minutes")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        Grab? grab = await _store.FindGrabAsync(hash, CancellationToken.None);
        grab!.State.Should().Be(GrabState.Failed);
        grab.FailureReason.Should().Be("no peers after 30 minutes");

        // Wanted again so a different release gets a turn, and blacklisted so it is not
        // the same broken one.
        (await _store.FindWantedAsync(new EpisodeKey(1, 1, 1), CancellationToken.None))!.State.Should().Be(WantedState.Wanted);
        (await _store.IsBlacklistedAsync(hash, "Some.Show.S01E01.1080p.WEB-DL", Now, CancellationToken.None)).Should().BeTrue();
        _engine.Removed.Should().Contain(hash);
    }

    [Fact]
    public async Task TransfersCycleAsync_IgnoresATorrentItNeverGrabbed()
    {
        _engine.Transfers = [Downloading("somebody-elses-hash", 1, 2, 3)];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        (await _store.TransfersAsync(CancellationToken.None)).Should().BeEmpty();
    }

    // --- helpers -----------------------------------------------------------------

    private async Task WantOneEpisodeAsync() => await WantEpisodesAsync(1);

    // The show carries one episode that is already on the server, because a show with
    // nothing is one this plugin leaves alone - see RefreshWantedAsync. It sits after the
    // wanted ones so their numbers, which several tests assert on, stay 1..count.
    private async Task WantEpisodesAsync(int count)
    {
        _library.Add(1, "Some Show", "/media/some-show",
        [
            .. Enumerable.Range(1, count).Select(number => (1, number, false)),
            (1, count + 1, true),
        ]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);
    }

    private async Task GrabOneAsync()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);
    }

    private static EngineTransfer Downloading(string hash, long done, long total, int peers) => new()
    {
        InfoHash = hash,
        State = EngineState.Downloading,
        BytesDone = done,
        BytesTotal = total,
        Peers = peers,
    };

    private static EngineTransfer Completed(string hash, string folder) => new()
    {
        InfoHash = hash,
        State = EngineState.Completed,
        BytesDone = 1000,
        BytesTotal = 1000,
        CompletedFolder = folder,
    };

    private static EngineTransfer Failed(string hash, string reason) => new()
    {
        InfoHash = hash,
        State = EngineState.Failed,
        FailureReason = reason,
    };

    private sealed class FakeLibrary : ILibraryQuery
    {
        private readonly List<LibraryShow> _shows = [];
        private readonly Dictionary<int, List<LibraryEpisode>> _episodes = [];

        /// <summary>
        /// The status defaults to what the record itself defaults to, so a test that does
        /// not care about it exercises the same value a server too old to answer produces.
        /// </summary>
        public void Add(
            int showId,
            string title,
            string? folder,
            IEnumerable<(int Season, int Episode, bool HasFile)> episodes,
            ShowStatus status = ShowStatus.Unknown)
        {
            List<LibraryEpisode> list = [.. episodes.Select(episode =>
                new LibraryEpisode(showId, episode.Season, episode.Episode, $"Episode {episode.Episode}", null, episode.HasFile))];

            _shows.Add(new LibraryShow(showId, title, 2026, "lib-1", folder, list.Count, list.Count(e => e.HasFile))
            {
                Status = status,
            });

            _episodes[showId] = list;
        }

        /// <summary>Gives one episode an air date. Everything else stays undated, as most test libraries are.</summary>
        public void SetAirDate(int showId, int season, int episode, DateTimeOffset airs)
        {
            List<LibraryEpisode> list = _episodes[showId];
            int index = list.FindIndex(known => known.SeasonNumber == season && known.EpisodeNumber == episode);

            list[index] = list[index] with { AirDate = airs };
        }

        public Task<IReadOnlyList<LibraryShow>> GetShowsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryShow>>(_shows);

        public Task<IReadOnlyList<LibraryEpisode>> GetEpisodesAsync(int showId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryEpisode>>(_episodes.GetValueOrDefault(showId, []));

        public Task<IReadOnlyList<LibraryFile>> GetFilesAsync(int showId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<LibraryFile>>([]);
    }

    private sealed class FakeSearch : IReleaseSearch
    {
        public IReadOnlyList<ReleaseInfo> Results { get; set; } = [];

        /// <summary>
        /// Give each query its own release. A store keys grabs by info hash, so tests
        /// spanning several episodes need several torrents - handing them all the same
        /// hash would silently collapse them into one grab and prove nothing.
        /// </summary>
        public bool UniquePerQuery { get; set; }

        public List<SearchQuery> Queries { get; } = [];

        public Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
        {
            Queries.Add(query);

            if (!UniquePerQuery)
                return Task.FromResult(Results);

            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(query.Text)));

            return Task.FromResult<IReadOnlyList<ReleaseInfo>>(
            [
                .. Results.Select(release => release with
                {
                    InfoHash = hash,
                    MagnetUri = $"magnet:?xt=urn:btih:{hash}",
                }),
            ]);
        }
    }

    /// <summary>Stands in for the sites: hands back where to get whatever it is asked about.</summary>
    private sealed class FakeResolver : IReleaseResolver
    {
        public ReleaseInfo? Answer { get; set; }

        public List<string> Asked { get; } = [];

        public Task<ReleaseInfo?> ResolveAsync(ReleaseInfo announced, CancellationToken ct)
        {
            Asked.Add(announced.Title);
            return Task.FromResult(Answer);
        }
    }

    private sealed class FakeFeed : IReleaseFeed
    {
        public IReadOnlyList<ReleaseInfo> Latest { get; set; } = [];

        public Task<IReadOnlyList<ReleaseInfo>> LatestAsync(CancellationToken ct) => Task.FromResult(Latest);
    }

    private sealed class FakeChooser : IReleaseChooser
    {
        public bool Accept { get; set; } = true;

        public IReadOnlyList<ReleaseInfo> LastCandidates { get; private set; } = [];

        /// <summary>
        /// Whether the orchestrator was willing to spend a season's bytes on this search.
        /// It is the orchestrator's decision, not the chooser's - the chooser only obeys
        /// it - so this is where the decision is observable.
        /// </summary>
        public bool LastAllowedSeasonPacks { get; private set; }

        public ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates, bool allowSeasonPacks)
        {
            LastCandidates = candidates;
            LastAllowedSeasonPacks = allowSeasonPacks;
            return Accept ? candidates.FirstOrDefault() : null;
        }
    }

    private sealed class FakeEngine : ITorrentEngine
    {
        public List<TorrentRequest> Added { get; } = [];
        public List<string> Removed { get; } = [];
        public IReadOnlyList<EngineTransfer> Transfers { get; set; } = [];

        /// <summary>
        /// Throws on the next Add and then behaves. Stands in for a swarm that will not
        /// answer, a disk that will not take the file, or anything else the engine can
        /// fail on - the orchestrator's job is the same whichever it was.
        /// </summary>
        public Exception? ThrowOnceWith { get; set; }

        // The source doubles as the info hash so a test can tie a request to a transfer
        // without inventing a mapping the real engine would not have.
        public Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
        {
            if (ThrowOnceWith is Exception failure)
            {
                ThrowOnceWith = null;
                throw failure;
            }

            Added.Add(request);
            return Task.FromResult(request.Source);
        }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            Removed.Add(infoHash);
            return Task.CompletedTask;
        }

        public List<string> Paused { get; } = [];

        public List<string> Resumed { get; } = [];

        public Task PauseAsync(string infoHash, CancellationToken ct)
        {
            Paused.Add(infoHash);
            return Task.CompletedTask;
        }

        public Task ResumeAsync(string infoHash, CancellationToken ct)
        {
            Resumed.Add(infoHash);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct) =>
            Task.FromResult(Transfers);
    }

    private sealed class FakeIntake : IIntakeHandoff
    {
        public bool Succeed { get; set; } = true;

        public List<(string Folder, EpisodeKey Key)> Moved { get; } = [];

        public Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct)
        {
            if (Succeed)
                Moved.Add((completedFolder, key));

            return Task.FromResult(Succeed);
        }
    }
}
