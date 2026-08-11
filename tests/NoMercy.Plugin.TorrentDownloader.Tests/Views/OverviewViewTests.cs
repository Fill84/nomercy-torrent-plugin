// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The page the menu entry lands on. It has to answer "is this working" without the reader
/// opening another tab.
/// </summary>
public class OverviewViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static Transfer Transfer(string hash, long done = 500, long total = 1000, bool paused = false) => new()
    {
        InfoHash = hash,
        BytesDone = done,
        BytesTotal = total,
        Peers = 8,
        Paused = paused,
        UpdatedAt = Now,
    };

    private static Grab Grab(string hash, string title) => new()
    {
        InfoHash = hash,
        Key = new EpisodeKey(1, 1, 1),
        ReleaseTitle = title,
        Indexer = "site-a",
        GrabbedAt = Now,
    };

    private static WantedEpisode Wanted(int episode, WantedState state = WantedState.Wanted) => new()
    {
        Key = new EpisodeKey(1, 1, episode),
        ShowTitle = "Some Show",
        State = state,
    };

    private static PluginView Build(
        IReadOnlyList<Transfer>? transfers = null,
        IReadOnlyList<Grab>? grabs = null,
        IReadOnlyList<WantedEpisode>? wanted = null,
        IReadOnlyList<HistoryEntry>? history = null,
        IReadOnlyList<FollowableShow>? shows = null,
        IReadOnlyList<string>? ungranted = null) =>
        OverviewView.Build(transfers ?? [], grabs ?? [], wanted ?? [], history ?? [], shows ?? [], ungranted ?? []);

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = Build(
            [Transfer("abc")],
            [Grab("abc", "Some.Show.S01E01.1080p")],
            [Wanted(2)],
            shows: [new FollowableShow(42, "Never Watched", Followed: false)],
            ungranted: ["scnsrc.me"]);

        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(Build()).Should().Contain(node => node.Id == "tab-sources");
    }

    // The first question is "is it doing anything", and answering that by counting rows is
    // work the page should have done for the reader.
    [Fact]
    public void Build_SaysWhatIsHappeningInOneLine()
    {
        PluginView view = Build([Transfer("abc"), Transfer("def", paused: true)], wanted: [Wanted(1), Wanted(2)]);

        PluginNodes.Says(view, "1 downloading").Should().BeTrue();
        PluginNodes.Says(view, "1 paused").Should().BeTrue();
        PluginNodes.Says(view, "2 episodes wanted").Should().BeTrue();
    }

    // The whole reason this page exists. A host waiting on a grant is the difference between
    // a plugin that is searching and one that only looks like it is - and it is invisible
    // everywhere else until somebody reads the server log.
    [Fact]
    public void Build_SaysWhenItIsWaitingOnHostAccess()
    {
        PluginView view = Build(ungranted: ["www.scnsrc.me"]);

        PluginNodes.All(view).Should().Contain(node => node.Id == "overview-grants");
        PluginNodes.Says(view, "www.scnsrc.me").Should().BeTrue();
    }

    [Fact]
    public void Build_SaysNothingAboutAccessWhenEverythingIsGranted()
    {
        PluginNodes.All(Build()).Should().NotContain(node => node.Id == "overview-attention");
    }

    [Fact]
    public void Build_CountsTheEpisodesItSearchedForAndCouldNotFind()
    {
        PluginView view = Build(wanted: [Wanted(1), Wanted(2, WantedState.Unavailable), Wanted(3, WantedState.Unavailable)]);

        PluginNodes.Says(view, "2 episodes have been searched for and not found").Should().BeTrue();
    }

    [Fact]
    public void Build_ShowsWhatIsMovingWithoutOfferingToDestroyIt()
    {
        PluginView view = Build([Transfer("abc")], [Grab("abc", "Some.Show.S01E01.1080p")]);

        PluginNodes.Says(view, "Some.Show.S01E01.1080p").Should().BeTrue();

        // Pausing and cancelling live on the downloads page. A glance page that can also
        // destroy things is one people stop glancing at.
        PluginNodes.All(view).Where(node => node.Action is not null)
            .Should().OnlyContain(node => node.Action!.Confirm == null);
    }

    [Fact]
    public void Build_LinksToTheFullListRatherThanGrowingWithoutEnd()
    {
        List<Transfer> many = [.. Enumerable.Range(1, OverviewView.DigestLength + 3).Select(number => Transfer($"hash-{number}"))];

        PluginComponent more = PluginNodes.All(Build(many)).Should()
            .ContainSingle(node => node.Id == "overview-now-more").Which;

        more.Action!.Type.Should().Be(PluginActionType.Navigate);
        more.Action.Payload[PluginNavigation.RouteKey].Should().Be("/downloads");
    }

    [Fact]
    public void Build_SaysNothingIsDownloadingRatherThanLookingBroken()
    {
        PluginNodes.Says(Build(), "Nothing is downloading right now.").Should().BeTrue();
    }

    // The counterpart of the rule that keeps a first run from being a thousand downloads:
    // without somewhere to say "except this one", the plugin can never start a show at all.
    [Fact]
    public void Build_OffersToFollowAShowWithNothingOnTheServer()
    {
        PluginView view = Build(shows: [new FollowableShow(42, "Never Watched", Followed: false)]);

        PluginComponent button = PluginNodes.All(view).Should()
            .ContainSingle(node => node.Id == "overview-follow-42").Which;

        button.Action!.Payload["method"].Should().Be("FollowShow/42");
        PluginNodes.Says(view, "Never Watched").Should().BeTrue();
    }

    // Tiles, not a column of buttons: a card is the one component with a surface of its own,
    // so twenty shows read as twenty things. The whole tile is the button, which is why the
    // subtitle has to say what pressing it does.
    [Fact]
    public void Build_DrawsTheShowsAsTilesThatSayWhatAClickDoes()
    {
        PluginView view = Build(shows: [new FollowableShow(42, "Never Watched", Followed: false)]);

        PluginNodes.All(view).Should().Contain(node => node.Component == Ui.GridComponent);

        PluginComponent tile = PluginNodes.All(view).Single(node => node.Id == "overview-follow-42");

        tile.Component.Should().Be(Ui.CardComponent);
        tile.Props["subtitle"].Should().Be("Click to follow");
    }

    [Fact]
    public void Build_OffersToStopFollowingAShowItIsAlreadyFollowing()
    {
        PluginView view = Build(shows: [new FollowableShow(42, "Asked For", Followed: true)]);

        PluginNodes.All(view).Should().Contain(node => node.Id == "overview-unfollow-42");
        PluginNodes.All(view).Should().NotContain(node => node.Id == "overview-follow-42");
    }

    // A library where every show has files is the normal case, and a heading over an empty
    // list reads as something being broken.
    [Fact]
    public void Build_SaysNothingAboutUnstartedShowsWhenThereAreNone()
    {
        PluginNodes.All(Build()).Should().NotContain(node => node.Id == "overview-unstarted");
    }

    [Fact]
    public void Build_AsksTheClientToComeBackBecauseTheseNumbersMove()
    {
        Build([Transfer("abc")]).RefreshInterval.Should().BeInRange(1, 60);
    }
}
