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
/// <summary>The buttons: search now, paste a link, pause, resume, cancel, allow again - and the history they write.</summary>
public class OwnerActionTests : DownloadOrchestratorTestBase
{
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

    // --- history ---------------------------------------------------------------------

    /// <summary>
    /// Choosing something is not news.
    ///
    /// <para>
    /// Most of what is chosen off a public tracker turns out to have nobody seeding it, and
    /// a history line per choice buried the two downloads that actually arrived under a
    /// dozen that never started. Written when bytes appear instead.
    /// </para>
    /// </summary>
    [Fact]
    public async Task SearchCycleAsync_WritesNoHistoryUntilSomethingActuallyArrives()
    {
        await GrabOneAsync();

        _store.History.Should().BeEmpty();
    }

    [Fact]
    public async Task TransfersCycleAsync_WritesDownWhatItGrabbedOnceBytesAppear()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Downloading }];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        HistoryEntry entry = _store.History.Should().ContainSingle().Subject;
        entry.Event.Should().Be(HistoryEvent.Grabbed);
        entry.Indexer.Should().Be("site-a");
    }

    // Once per download, not once per tick.
    [Fact]
    public async Task TransfersCycleAsync_WritesThatLineOnlyOnce()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [new EngineTransfer { InfoHash = hash, State = EngineState.Downloading }];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _store.History.Should().ContainSingle();
    }

    /// <summary>
    /// The engine holds an open FileStream per file for as long as it holds the torrent,
    /// and Windows will not rename a file somebody has open without share-delete. So the
    /// move threw IOException, the mover swallowed it and returned null, and the grab stayed
    /// Downloaded - forever, because the retry only fires while the engine still reports the
    /// transfer. Two finished episodes sat in the download folder for a day that way, each
    /// holding one of the five concurrent download slots.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_LetsGoOfTheTorrentBeforeMovingItsFiles()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [Completed(hash, "/downloads/Some.Show.S01E01.mkv")];

        List<string> trace = [];
        _engine.Trace = trace;
        _intake.Trace = trace;

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        trace.Should().Equal($"released {hash}", "moved /downloads/Some.Show.S01E01.mkv");
    }

    /// <summary>
    /// The bytes are on disk and verified; the engine has nothing left to contribute. It
    /// used to be the only thing that could start an import, so a grab that reached
    /// Downloaded and then failed its move was retried only while the engine still held the
    /// torrent - and never again after a restart. Two episodes were stranded there,
    /// complete, with no way back: grabbed before the source was kept, so nothing could
    /// hand them to the engine either.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_FinishesADownloadTheEngineNoLongerKnowsAbout()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [Completed(hash, "/downloads/Some.Show.S01E01.mkv")];

        // The move fails once, which is how a grab gets to Downloaded and stays there.
        _intake.Succeed = false;
        await Orchestrator().TransfersCycleAsync(CancellationToken.None);
        (await _store.FindGrabAsync(hash, CancellationToken.None))!.State.Should().Be(GrabState.Downloaded);

        // The engine is emptied, as a restart empties it.
        _engine.Transfers = [];
        _intake.Succeed = true;

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _intake.Moved.Should().ContainSingle().Which.Folder.Should().Be("/downloads/Some.Show.S01E01.mkv");
        (await _store.FindGrabAsync(hash, CancellationToken.None))!.State.Should().Be(GrabState.Imported);
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
        _engine.Transfers = [Failed(hash, "the disk is full")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        HistoryEntry entry = _store.History.Should().ContainSingle(entry => entry.Event == HistoryEvent.Failed).Subject;
        entry.Detail.Should().Be("the disk is full");
    }

    /// <summary>
    /// A swarm with nobody in it is not an event. It is the ordinary weather of a public
    /// tracker, the episode goes straight back on the queue, and nobody can act on it - so a
    /// line saying so is noise hiding the failures somebody could act on.
    /// </summary>
    [Fact]
    public async Task TransfersCycleAsync_SaysNothingWhenNobodyWasSeedingIt()
    {
        await GrabOneAsync();
        string hash = (await _store.ActiveGrabsAsync(CancellationToken.None))[0].InfoHash;
        _engine.Transfers = [Failed(hash, "no peer offered this torrent's contents within the time allowed")];

        await Orchestrator().TransfersCycleAsync(CancellationToken.None);

        _store.History.Should().BeEmpty();

        // Still put back, still skipped, still removed - only the telling is dropped.
        (await _store.WantedAsync(10, CancellationToken.None)).Should().ContainSingle()
            .Which.State.Should().Be(WantedState.Wanted);
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

}
