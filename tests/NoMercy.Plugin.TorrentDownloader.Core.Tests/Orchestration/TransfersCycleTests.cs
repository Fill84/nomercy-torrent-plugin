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
/// <summary>What the engine reports, and what becomes of it: imported, retried, or put back.</summary>
public class TransfersCycleTests : DownloadOrchestratorTestBase
{
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

    // --- surviving a restart -------------------------------------------------------------

    /// <summary>
    /// The engine keeps its torrents in memory, so a restart empties it while the store
    /// still holds every grab. Nothing put them back, and a download that had already
    /// finished was then stranded: on disk, marked Downloaded, never asked about again - so
    /// the import never ran and no encode was ever queued. Three finished episodes sat like
    /// that on a real server.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_HandsBackADownloadTheEngineHasForgotten()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        // A restart: the store remembers, the engine does not.
        _engine.Added.Clear();
        _engine.Transfers = [];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _engine.Added.Should().ContainSingle()
            .Which.Source.Should().Be(Release().MagnetUri, "the grab remembers where it came from");
    }

    [Fact]
    public async Task TransfersCycleAsync_LeavesADownloadTheEngineAlreadyHoldsAlone()
    {
        await WantOneEpisodeAsync();
        _search.Results = [Release()];
        await Orchestrator().SearchCycleAsync(CancellationToken.None);

        string hash = _engine.Added.Should().ContainSingle().Subject.Source;
        _engine.Added.Clear();
        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Downloading }];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _engine.Added.Should().BeEmpty("asking every minute for a torrent it already has is not free");
    }

    // A grab written before the source was kept cannot be resumed, and guessing would
    // download the wrong episode.
    [Fact]
    public async Task TransfersCycleAsync_LeavesAGrabWithNoSourceAlone()
    {
        await _store.AddGrabAsync(new Grab
        {
            InfoHash = "old",
            Key = new EpisodeKey(1, 1, 1),
            ReleaseTitle = "From.Before.The.Source.Was.Kept",
            Indexer = "site-a",
            GrabbedAt = Now,
        }, CancellationToken.None);

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _engine.Added.Should().BeEmpty();
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

        TransfersCycle cycle = await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        cycle.Imported.Should().Be(1);
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

        (await Orchestrator().TransfersCycleAsync(CancellationToken.None)).Imported.Should().Be(0);

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
        (await orchestrator.TransfersCycleAsync(CancellationToken.None)).Imported.Should().Be(1);
    }

    /// <summary>
    /// A failed download leaves its episodes exactly as missing as they were before
    /// anything was grabbed, so the caller is told how many went back and searches for them
    /// at once. Waiting for the next cadence is the plugin sitting on work it already knows
    /// about - on a six-hourly search, half a day per dead swarm.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_ReportsTheEpisodesAFailedDownloadPutBack()
    {
        string hash = await GrabAPackAsync(4);
        _engine.Transfers = [Failed(hash, "no peers after 30 minutes")];

        TransfersCycle cycle = await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        // Every episode the pack covered, not just the one that triggered it.
        cycle.PutBack.Should().Be(4);
    }

    [Fact]
    public async Task TransfersCycleAsync_ReportsNothingPutBackWhenNothingFailed()
    {
        await GrabOneAsync();
        _engine.Transfers = [Completed(_engine.Added[0].Source, "/downloads/some.show.s01e01")];

        (await Orchestrator().TransfersCycleAsync(CancellationToken.None)).PutBack.Should().Be(0);
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

}
