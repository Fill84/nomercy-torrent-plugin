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
        IReadOnlyList<string>? ungranted = null,
        int shows = 0) =>
        OverviewView.Build(
            transfers ?? [],

            // A transfer this plugin no longer holds a grab for is left off the page - it
            // has no name to show, only its info hash. So a test that only cares about the
            // counting gets a grab per transfer rather than having to say so every time.
            grabs ?? [.. (transfers ?? []).Select(transfer => Grab(transfer.InfoHash, $"Release.{transfer.InfoHash}"))],
            wanted ?? [],
            history ?? [],
            ungranted ?? [],
            shows);

    /// <summary>
    /// The two numbers this plugin puts in front of somebody are episodes here and shows on
    /// the next tab. Read one after the other - 42, then a list of 25 - they look like a
    /// contradiction, and the reader is left doing the arithmetic to find out it is not one.
    /// </summary>
    [Fact]
    public void Build_SaysHowManyShowsTheWantedEpisodesAreSpreadAcross()
    {
        PluginNodes.Says(Build(wanted: [Wanted(1), Wanted(2)], shows: 25), "2 episodes wanted across 25 shows")
            .Should().BeTrue();
    }

    [Fact]
    public void Build_SaysFromOneShowRatherThanAcrossOne()
    {
        PluginNodes.Says(Build(wanted: [Wanted(1)], shows: 1), "1 episode wanted from 1 show").Should().BeTrue();
    }

    /// <summary>
    /// "0 episodes wanted across 25 shows" makes an idle plugin sound busy, on the one line
    /// meant to be read at a glance.
    /// </summary>
    [Fact]
    public void Build_LeavesTheShowsOutWhenNothingIsWanted()
    {
        PluginNodes.Says(Build(shows: 25), "across").Should().BeFalse();
    }

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = Build(
            [Transfer("abc")],
            [Grab("abc", "Some.Show.S01E01.1080p")],
            [Wanted(2)],
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

    // Idle is the common case, and a page that looks like an error when nothing is happening
    // trains a reader to ignore it. Said in the summary line rather than in a section of its
    // own - see Build_DoesNotSpendHalfAScreenSayingNothingIsDownloading.
    [Fact]
    public void Build_SaysNothingIsDownloadingRatherThanLookingBroken()
    {
        PluginNodes.Says(Build(wanted: [Wanted(1)]), "0 downloading").Should().BeTrue();
    }

    /// <summary>
    /// The page used to count the library rows with no episode on the server, so an owner
    /// would know the plugin was passing over them. Those shows are not the plugin's, and
    /// reporting on them here is the page that answers "what is this plugin doing" spending
    /// space on somebody else's business. Following one by name lives on Shows.
    /// </summary>
    [Fact]
    public void Build_SaysNothingAboutShowsThatAreNotThePluginsBusiness()
    {
        PluginNodes.All(Build()).Should().NotContain(node => node.Id == "overview-unstarted");
    }

    [Fact]
    public void Build_AsksTheClientToComeBackBecauseTheseNumbersMove()
    {
        Build([Transfer("abc")]).RefreshInterval.Should().BeInRange(1, 60);
    }
}
