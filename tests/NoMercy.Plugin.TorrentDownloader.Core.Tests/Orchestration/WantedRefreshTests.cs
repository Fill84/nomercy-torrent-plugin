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
/// <summary>Which shows this plugin holds, and which episodes of them it wants.</summary>
public class WantedRefreshTests : DownloadOrchestratorTestBase
{
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

}
