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
        TextOf(QueueView.Build([])).Should().Contain("Nothing is missing");
    }

    [Fact]
    public void Build_CountsTheQueueAndAdmitsWhenItIsTruncated()
    {
        PluginView view = QueueView.Build([.. Enumerable.Range(1, 200).Select(number => Wanted(number))]);

        // A first run on a library with years of gaps wants hundreds. Rendering all of them is
        // a page nobody can read, and pretending there are 25 is a lie.
        TextOf(view).Should().Contain($"Wanted ({QueueView.PreviewLength} of 200)");
    }

    [Fact]
    public void Build_LabelsEachWantedEpisodeWithWhereItStands()
    {
        PluginView view = QueueView.Build([Wanted(1), Wanted(2, WantedState.Grabbed), Wanted(3, WantedState.Unavailable)]);

        List<string> text = TextOf(view);

        text.Should().Contain("Wanted");
        text.Should().Contain("Downloading");
        text.Should().Contain("Not found");
    }

    // An episode that has not aired is not something the plugin is failing to find, and a
    // page that cannot tell those apart makes the owner chase a problem that does not exist.
    [Fact]
    public void Build_SaysAnUnairedEpisodeIsStillComingRatherThanMissing()
    {
        WantedEpisode soon = Wanted(4) with { AirDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(3)) };

        PluginNodes.Says(QueueView.Build([soon]), "Airs ").Should().BeTrue();
    }

    // Seen on a real server: every row read "456 S00E01".
    [Fact]
    public void Build_NamesTheEpisodeSlotWithoutLeakingTheShowId()
    {
        PluginView view = QueueView.Build([Wanted(2)]);

        TextOf(view).Should().Contain("S01E02").And.NotContain("1 S01E02");
    }

    // The cadence works least-recently-searched first, ten at a time, which is the right
    // order for a machine and the wrong one for somebody who wants tonight's episode.
    [Fact]
    public void Build_OffersToSearchOneEpisodeNow()
    {
        PluginComponent button = PluginNodes.All(QueueView.Build([Wanted(5)]))
            .Should().ContainSingle(node => node.Component == Ui.ButtonComponent && node.Id.StartsWith("queue-search-now")).Which;

        button.Action!.Payload["method"].Should().Be("SearchNow/1/1/5");
    }
}
