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

    private DownloadOrchestrator Orchestrator(OrchestratorOptions? options = null) => new(
        _library,
        _store,
        _search,
        _chooser,
        _engine,
        _intake,
        options ?? new OrchestratorOptions { DownloadFolder = "/downloads" },
        new PrivateTrackerRegistry([]),
        () => Now);

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

    [Fact]
    public async Task RefreshWantedAsync_SkipsAShowWithNoFolderToDownloadInto()
    {
        _library.Add(showId: 1, "Homeless Show", folder: null, episodes: [(1, 1, false)]);

        (await Orchestrator().RefreshWantedAsync(CancellationToken.None)).Wanted.Should().Be(0);
    }

    // The rule that keeps a first run from being a thousand downloads: a show nobody has
    // a single episode of is a show nobody asked for. The library lists everything the
    // metadata provider knows about, not everything the owner wants.
    [Fact]
    public async Task RefreshWantedAsync_LeavesAloneAShowWithNothingOnTheServerYet()
    {
        _library.Add(showId: 1, "Never Watched", folder: "/media/never", episodes:
        [
            (1, 1, HasFile: false),
            (1, 2, HasFile: false),
        ]);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(0);
        (await _store.WantedAsync(10, CancellationToken.None)).Should().BeEmpty();
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
        refresh.ShowsFollowed.Should().Be(1);
        refresh.ShowsNotStarted.Should().Be(0, "it was asked for, so it is not being left alone");
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
        refresh.ShowsNotStarted.Should().Be(1);
    }

    // Seen on a real server: the page listed 65 shows to follow while the log said 12,
    // and among the 65 were shows whose missing episodes were in the queue directly
    // above. The page was reading the host's HaveEpisodeCount, which is zero for shows
    // that plainly have episodes. One question, one answer: whoever decides, records.
    [Fact]
    public async Task RefreshWantedAsync_WritesDownTheShowsItLeftAloneAndNoOthers()
    {
        _library.Add(showId: 1, "Started", folder: "/media/started", episodes: [(1, 1, true), (1, 2, false)]);
        _library.Add(showId: 2, "Never Watched", folder: "/media/never", episodes: [(1, 1, false)]);

        await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        IReadOnlyList<UnstartedShow> unstarted = await _store.UnstartedShowsAsync(CancellationToken.None);
        unstarted.Should().ContainSingle();
        unstarted[0].ShowId.Should().Be(2);
        unstarted[0].Title.Should().Be("Never Watched");
    }

    [Fact]
    public async Task RefreshWantedAsync_StopsListingAShowOnceItIsFollowed()
    {
        _library.Add(showId: 7, "Asked For", folder: "/media/asked", episodes: [(1, 1, false)]);

        await Orchestrator(new OrchestratorOptions { DownloadFolder = "/downloads", FollowedShowIds = [7] })
            .RefreshWantedAsync(CancellationToken.None);

        (await _store.UnstartedShowsAsync(CancellationToken.None)).Should().BeEmpty();
    }

    // Skipping quietly is how a user concludes the plugin is broken. The counts are what
    // the log line is built from.
    [Fact]
    public async Task RefreshWantedAsync_CountsTheShowsItLeftAlone()
    {
        _library.Add(showId: 1, "Started", folder: "/media/started", episodes: [(1, 1, true), (1, 2, false)]);
        _library.Add(showId: 2, "Never Watched", folder: "/media/never", episodes: [(1, 1, false)]);
        _library.Add(showId: 3, "Also Never", folder: "/media/also", episodes: [(1, 1, false)]);

        WantedRefresh refresh = await Orchestrator().RefreshWantedAsync(CancellationToken.None);

        refresh.Wanted.Should().Be(1);
        refresh.ShowsFollowed.Should().Be(1);
        refresh.ShowsNotStarted.Should().Be(2);
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

    // --- season packs ------------------------------------------------------------

    [Fact]
    public async Task SearchCycleAsync_ASeasonPackIsOneGrabThatSettlesEveryEpisodeItCovers()
    {
        await WantEpisodesAsync(4);
        _search.Results = [Release("Some.Show.S01.1080p.WEB-DL", "packhash")];

        int grabbed = await Orchestrator().SearchCycleAsync(CancellationToken.None);

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

        int grabbed = await Orchestrator().SearchCycleAsync(CancellationToken.None);

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

        (await Orchestrator().SearchCycleAsync(CancellationToken.None)).Should().Be(0);

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

        int grabbed = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 3,
            SearchBatchSize = 20,
        }).SearchCycleAsync(CancellationToken.None);

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

        (await orchestrator.SearchCycleAsync(CancellationToken.None)).Should().Be(0);
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

        public void Add(int showId, string title, string? folder, IEnumerable<(int Season, int Episode, bool HasFile)> episodes)
        {
            List<LibraryEpisode> list = [.. episodes.Select(episode =>
                new LibraryEpisode(showId, episode.Season, episode.Episode, $"Episode {episode.Episode}", null, episode.HasFile))];

            _shows.Add(new LibraryShow(showId, title, 2026, "lib-1", folder, list.Count, list.Count(e => e.HasFile)));
            _episodes[showId] = list;
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

        // The source doubles as the info hash so a test can tie a request to a transfer
        // without inventing a mapping the real engine would not have.
        public Task<string> AddAsync(TorrentRequest request, CancellationToken ct)
        {
            Added.Add(request);
            return Task.FromResult(request.Source);
        }

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            Removed.Add(infoHash);
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
