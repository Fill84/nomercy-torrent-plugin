// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The page the whole overhaul was for: everything grouped by the thing an owner thinks in.
/// </summary>
public class ShowsViewTests
{
    private static ShowSummary Show(
        int id = 1,
        string title = "Some Show",
        int missing = 0,
        int downloading = 0,
        bool started = true,
        bool followed = false,
        DateTimeOffset? arrived = null,
        ShowStatus status = ShowStatus.Returning) =>
        new(id, title, missing, downloading, arrived, started, followed) { Status = status };

    private static WantedEpisode Wanted(int episode, WantedState state = WantedState.Wanted) => new()
    {
        Key = new EpisodeKey(1, 1, episode),
        ShowTitle = "Some Show",
        EpisodeTitle = $"Episode {episode}",
        State = state,
    };

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginNodes.All(ShowsView.Build([Show(missing: 2)])).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(ShowsView.Build([])).Should().Contain(node => node.Id == "tab-overview");
    }

    [Fact]
    public void Build_SaysThereAreNoShowsRatherThanShowingAnEmptyTable()
    {
        PluginNodes.All(ShowsView.Build([])).Should().Contain(node => node.Id == "shows-empty");
    }

    // The three questions a per-episode queue makes you reconstruct by scanning for a name.
    [Fact]
    public void Build_AnswersWhatIsMissingWhatIsRunningAndWhatArrived()
    {
        PluginComponent row = PluginNodes.TableRows(
            ShowsView.Build([Show(missing: 3, downloading: 1, arrived: DateTimeOffset.UtcNow.AddHours(-2))]))
            .Should().ContainSingle().Which;

        PluginNodes.Cell(row, "missing").Should().Be("3");
        PluginNodes.Cell(row, "downloading").Should().Be("1");
        PluginNodes.Cell(row, "arrived").Should().Be("2 h ago");
    }

    // A zero in every column is noise. An empty cell reads as "nothing to say here", which
    // is what it means.
    [Fact]
    public void Build_LeavesACountBlankRatherThanDrawingZero()
    {
        PluginComponent row = PluginNodes.TableRows(ShowsView.Build([Show()])).Should().ContainSingle().Which;

        PluginNodes.Cell(row, "missing").Should().Be("");
        PluginNodes.Cell(row, "downloading").Should().Be("");
    }

    /// <summary>
    /// "Waiting" beats "Airing" for a show the owner asked for and none of which has
    /// arrived. Both have nothing missing, and only one of them means an episode has ever
    /// landed.
    /// </summary>
    [Fact]
    public void Build_TellsAShowWithNothingYetApartFromOneThatIsUpToDate()
    {
        PluginNodes.Cell(PluginNodes.TableRows(ShowsView.Build([Show(started: false)])).Single(), "state")
            .Should().Be("Waiting");

        PluginNodes.Cell(PluginNodes.TableRows(ShowsView.Build([Show(started: true)])).Single(), "state")
            .Should().Be("Airing");
    }

    /// <summary>
    /// Only a show the owner followed by hand can be finished and still on this page - the
    /// refresh passes over every other one. Which of the two it is is the thing worth
    /// knowing about a show that stopped, so they are not both "Complete".
    /// </summary>
    [Theory]
    [InlineData(ShowStatus.Ended, "Ended")]
    [InlineData(ShowStatus.Canceled, "Cancelled")]
    public void Build_NamesWhichWayAFollowedShowStopped(ShowStatus status, string expected)
    {
        PluginNodes.Cell(PluginNodes.TableRows(ShowsView.Build([Show(status: status)])).Single(), "state")
            .Should().Be(expected);
    }

    /// <summary>
    /// The one entry point to a show this page deliberately does not list. Without it the
    /// two rules that keep the list honest also make it impossible to start a new show.
    /// </summary>
    [Fact]
    public void Build_TakesTheNameOfAShowToFollow()
    {
        PluginNodes.All(ShowsView.Build([])).Should().Contain(node => node.Id == "shows-follow-form");
    }

    // A list ordered by title alone buries the one show that is stuck behind twenty that are
    // fine.
    [Fact]
    public void Build_PutsWhatIsBusyAndWhatIsShortAboveTheRest()
    {
        PluginView view = ShowsView.Build(
        [
            Show(1, "Aardvark"),
            Show(2, "Zebra", missing: 4),
            Show(3, "Mongoose", downloading: 1),
        ]);

        PluginNodes.TableRows(view).Select(row => PluginNodes.Cell(row, "show"))
            .Should().Equal("Mongoose", "Zebra", "Aardvark");
    }

    [Fact]
    public void Build_LeadsToTheShowsOwnPage()
    {
        PluginComponent row = PluginNodes.TableRows(ShowsView.Build([Show(42)])).Should().ContainSingle().Which;

        row.Action!.Type.Should().Be(PluginActionType.Navigate);
        row.Action.Payload[PluginNavigation.RouteKey].Should().Be("/shows/42");
    }

    // --- one show ------------------------------------------------------------------

    [Fact]
    public void Detail_IsHeadedByTheShowRatherThanByTheWordShow()
    {
        PluginView view = ShowsView.Detail(Show(title: "Silo"), [], []);

        PluginNodes.Words(view.Components!.Single().Items[0]).Should().Contain("Silo");
    }

    [Fact]
    public void Detail_ListsWhatIsMissingWithAWayToSearchForOne()
    {
        PluginView view = ShowsView.Detail(Show(missing: 1), [Wanted(4)], []);

        PluginComponent row = PluginNodes.TableRows(view).Should().ContainSingle().Which;

        PluginNodes.Cell(row, "episode").Should().Be("S01E04");
        PluginNodes.Cell(row, "title").Should().Be("Episode 4");
        row.Action!.Payload["method"].Should().Be("SearchNow/1/1/4");
    }

    // The one button that decides whether the plugin looks at this show at all. It moved
    // here from the overview, because deciding about a show belongs on the show.
    [Fact]
    public void Detail_OffersToFollowAShowItIsLeavingAlone()
    {
        PluginComponent button = PluginNodes.All(ShowsView.Detail(Show(42, started: false), [], []))
            .Should().ContainSingle(node => node.Id == "show-follow-42").Which;

        button.Action!.Payload["method"].Should().Be("FollowShow/42");
    }

    [Fact]
    public void Detail_OffersToStopFollowingOneItIsAlreadyFollowing()
    {
        PluginView view = ShowsView.Detail(Show(42, followed: true), [], []);

        PluginNodes.All(view).Should().Contain(node => node.Id == "show-unfollow-42");
        PluginNodes.All(view).Should().NotContain(node => node.Id == "show-follow-42");
    }

    // "0 missing" against a show none of which has arrived reads as complete, which is the
    // opposite of what it means.
    [Fact]
    public void Detail_SaysWhyThereIsNothingYetForAShowThatWasAskedFor()
    {
        PluginNodes.Says(ShowsView.Detail(Show(started: false), [], []), "Nothing of this is on the server yet")
            .Should().BeTrue();
    }

    [Fact]
    public void Detail_SaysNothingAboutMissingEpisodesWhenThereAreNone()
    {
        PluginNodes.All(ShowsView.Detail(Show(), [], [])).Should().NotContain(node => node.Id == "show-missing");
    }
}
