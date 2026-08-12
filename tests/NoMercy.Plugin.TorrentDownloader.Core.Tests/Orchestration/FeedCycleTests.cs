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
/// <summary>Taking tonight's airing off a feed rather than asking for it by name.</summary>
public class FeedCycleTests : DownloadOrchestratorTestBase
{
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

}
