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
/// <summary>Asking the indexers, choosing between what comes back, and handing it to the engine.</summary>
public class SearchAndGrabTests : DownloadOrchestratorTestBase
{
    // --- as many swarms as possible ------------------------------------------------------

    /// <summary>
    /// A torrent found through one site announces to that site's trackers alone, and every
    /// peer on the others is simply never asked. The aggregator already merges what each
    /// indexer reported for one info hash; the owner's own list goes on top of that.
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_AnnouncesToTheOwnersTrackersAsWellAsTheIndexersOwn()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release() with { Trackers = ["udp://from-the-site:1337/announce"] }];

        await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            ExtraTrackers = ["udp://the-owners:80/announce"],
        }).SearchCycleAsync(CancellationToken.None);

        _engine.Added.Should().ContainSingle().Which.ExtraTrackers
            .Should().Equal("udp://from-the-site:1337/announce", "udp://the-owners:80/announce");
    }

    // One tracker spelled two ways is one tracker, and announcing to it twice is a request
    // for nothing.
    [Fact]
    public async Task SearchCycleAsync_AnnouncesToEachTrackerOnce()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release() with { Trackers = ["udp://shared:80/announce"] }];

        await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            ExtraTrackers = ["UDP://SHARED:80/announce", "udp://extra:80/announce"],
        }).SearchCycleAsync(CancellationToken.None);

        _engine.Added.Should().ContainSingle().Which.ExtraTrackers
            .Should().Equal("udp://shared:80/announce", "udp://extra:80/announce");
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
        }).SearchCycleAsync(CancellationToken.None)).Grabbed;

        // The bound that turns a first run on a library with years of gaps into a steady
        // stream rather than two hundred downloads fighting over one connection.
        grabbed.Should().Be(3);
    }

    /// <summary>
    /// One cycle asks about every show, not the first few. It used to take ten per cycle,
    /// which on a library behind on twenty shows meant most of them were not looked at for
    /// hours - indistinguishable, from outside, from a plugin working one show at a time.
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_AsksAboutEveryEpisodeThatCouldBeSearched()
    {
        await WantEpisodesAsync(40);
        _search.Results = [];

        SearchCycle cycle = await Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
        }).SearchCycleAsync(CancellationToken.None);

        cycle.Searched.Should().Be(40);
    }

    /// <summary>
    /// A download that has finished and is waiting on its move takes no bandwidth, no peer
    /// slot and no disk head. Counting it against the ceiling is how two stuck imports held
    /// two of five places for a day - and with one more downloading, that left room for one
    /// new grab per cycle. From outside, a plugin working through one show at a time.
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_DoesNotCountAFinishedDownloadWaitingOnItsMove()
    {
        await WantEpisodesAsync(10);
        _search.Results = [Release()];
        _search.UniquePerQuery = true;

        DownloadOrchestrator orchestrator = Orchestrator(new OrchestratorOptions
        {
            DownloadFolder = "/downloads",
            MaxConcurrentDownloads = 3,
        });

        await orchestrator.SearchCycleAsync(CancellationToken.None);

        foreach (Grab grab in await _store.ActiveGrabsAsync(CancellationToken.None))
            await _store.UpdateGrabAsync(grab.InfoHash, GrabState.Downloaded, null, null, CancellationToken.None);

        _engine.Added.Clear();

        // All three places are free again: nothing is downloading, three things are waiting
        // to be moved.
        (await orchestrator.SearchCycleAsync(CancellationToken.None)).Grabbed.Should().Be(3);
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

}
