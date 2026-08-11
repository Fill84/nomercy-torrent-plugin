// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>Every episode still wanted, and where each one stands.</summary>
public class QueueViewTests
{
    private static WantedEpisode Wanted(int episode, WantedState state = WantedState.Wanted) => new()
    {
        Key = new EpisodeKey(1, 1, episode),
        ShowTitle = "Some Show",
        EpisodeTitle = $"Episode {episode}",
        State = state,
    };

    private static List<string> TextOf(PluginView view) => [.. PluginNodes.Words(view)];

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginNodes.All(QueueView.Build([Wanted(1)])).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(QueueView.Build([])).Should().Contain(node => node.Id == "tab-downloads");
    }

    [Fact]
    public void Build_SaysSoWhenTheLibraryIsComplete()
    {
        TextOf(QueueView.Build([])).Should().Contain("Nothing to look for");
    }

    [Fact]
    public void Build_CountsTheQueueAndAdmitsWhenItIsTruncated()
    {
        PluginView view = QueueView.Build([.. Enumerable.Range(1, 200).Select(number => Wanted(number))]);

        // A first run on a library with years of gaps wants hundreds. Rendering all of them is
        // a page nobody can read, and pretending there are 25 is a lie.
        TextOf(view).Should().Contain("Searching for (200)");
        PluginNodes.TableRows(view).Should().HaveCount(QueueView.PreviewLength);
        TextOf(view).Should().Contain("200 episodes to look for");
    }

    [Fact]
    public void Build_LabelsEachWantedEpisodeWithWhereItStands()
    {
        PluginView view = QueueView.Build([Wanted(1), Wanted(2, WantedState.Grabbed), Wanted(3, WantedState.Unavailable)]);

        List<string> text = TextOf(view);

        text.Should().Contain("Queued");
        text.Should().Contain("Downloading");
        text.Should().Contain("Not found");
    }

    /// <summary>
    /// Asked about and told no is not the same as never asked, and without the difference
    /// the whole list reads the same for hours and looks like nothing is happening. This is
    /// the question the page could not answer: is it actually looking at this one?
    /// </summary>
    [Fact]
    public void Build_SaysWhenAnEpisodeHasAlreadyBeenLookedFor()
    {
        WantedEpisode asked = Wanted(1) with
        {
            SearchAttempts = 4,
            LastSearchedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        };

        List<string> text = TextOf(QueueView.Build([asked]));

        text.Should().Contain("Looking");
        text.Should().Contain("4", "the page says how often it has asked");
    }

    [Fact]
    public void Build_SaysNotYetForAnEpisodeNobodyHasAskedAbout()
    {
        TextOf(QueueView.Build([Wanted(1)])).Should().Contain("not yet");
    }

    /// <summary>
    /// Three questions, three sections. An owner who wants everything learns nothing from
    /// one list called "Wanted": which is it about to search, which cannot be searched yet,
    /// and which has it given up on.
    /// </summary>
    [Fact]
    public void Build_KeepsTheThreeStatesApart()
    {
        WantedEpisode soon = Wanted(4) with { AirDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)) };

        PluginView view = QueueView.Build([Wanted(1), soon, Wanted(3, WantedState.Unavailable)]);

        List<PluginComponent> nodes = [.. PluginNodes.All(view)];

        nodes.Should().Contain(node => node.Id == "queue-next");
        nodes.Should().Contain(node => node.Id == "queue-waiting");
        nodes.Should().Contain(node => node.Id == "queue-unavailable");
        PluginNodes.Says(view, "1 episode to look for").Should().BeTrue();
        PluginNodes.Says(view, "1 waiting to air").Should().BeTrue();
        PluginNodes.Says(view, "1 given up on").Should().BeTrue();
    }

    // A library with nothing waiting must not carry a heading over an empty table.
    [Fact]
    public void Build_LeavesOutTheSectionsThatHaveNothingInThem()
    {
        List<PluginComponent> nodes = [.. PluginNodes.All(QueueView.Build([Wanted(1)]))];

        nodes.Should().NotContain(node => node.Id == "queue-waiting");
        nodes.Should().NotContain(node => node.Id == "queue-unavailable");
    }

    // An episode that has not aired is not something the plugin is failing to find, and a
    // page that cannot tell those apart makes the owner chase a problem that does not exist.
    [Fact]
    public void Build_SaysAnUnairedEpisodeIsStillComingRatherThanMissing()
    {
        WantedEpisode soon = Wanted(4) with { AirDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)) };

        PluginView view = QueueView.Build([soon]);

        PluginNodes.Says(view, "Waiting to air (1)").Should().BeTrue();
        PluginNodes.Says(view, "has not gone out yet").Should().BeTrue();
    }

    // Seen on a real server: every row read "456 S00E01".
    [Fact]
    public void Build_NamesTheEpisodeSlotWithoutLeakingTheShowId()
    {
        PluginView view = QueueView.Build([Wanted(2)]);

        TextOf(view).Should().Contain("S01E02").And.NotContain("1 S01E02");
    }

    // The cadence works least-recently-searched first, ten at a time, which is the right
    // order for a machine and the wrong one for somebody who wants tonight's episode. The
    // row itself carries it now, so twenty-five of these do not read as a column of buttons.
    [Fact]
    public void Build_OffersToSearchOneEpisodeNow()
    {
        PluginComponent row = PluginNodes.TableRows(QueueView.Build([Wanted(5)])).Should().ContainSingle().Which;

        row.Action!.Payload["method"].Should().Be("SearchNow/1/1/5");
    }

    // A clickable row does not announce itself the way a button labelled "Search now" did,
    // so the page has to say what a click does. Without this the action is invisible.
    [Fact]
    public void Build_SaysThatARowCanBeClicked()
    {
        PluginNodes.Says(QueueView.Build([Wanted(1)]), "Click one to ask now").Should().BeTrue();
    }

    // A table lines its columns up; a list of wrapping rows re-flows differently on every
    // row, which is what made twenty-five wanted episodes unreadable.
    [Fact]
    public void Build_LinesTheEpisodesUpInColumns()
    {
        PluginNodes.All(QueueView.Build([Wanted(1)])).Should().Contain(node => node.Component == Ui.TableComponent);
    }
}
