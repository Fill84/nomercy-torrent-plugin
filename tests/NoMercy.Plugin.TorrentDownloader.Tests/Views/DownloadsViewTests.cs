// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

public class DownloadsViewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private static Transfer Transfer(string hash, long done, long total, int peers = 8) => new()
    {
        InfoHash = hash,
        BytesDone = done,
        BytesTotal = total,
        Peers = peers,
        UpdatedAt = Now,
    };

    private static Grab Grab(string hash, int episode, string title) => new()
    {
        InfoHash = hash,
        Key = new EpisodeKey(1, 1, episode),
        ReleaseTitle = title,
        Indexer = "site-a",
        GrabbedAt = Now,
    };

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
        PluginView view = DownloadsView.Build(
            [Transfer("abc", 500, 1000)],
            [Grab("abc", 1, "Some.Show.S01E01.1080p")],
            [Wanted(2)]);

        // A tag the client does not know renders as an "unsupported component" notice,
        // which is a page of apologies rather than a page.
        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_ShowsWhatIsDownloadingAndHowFar()
    {
        PluginView view = DownloadsView.Build(
            [Transfer("abc", 500, 1000)],
            [Grab("abc", 1, "Some.Show.S01E01.1080p")],
            []);

        IEnumerable<string> text = TextOf(view);

        text.Should().Contain("Some.Show.S01E01.1080p");
        text.Should().Contain("50%");
        text.Should().Contain("8 peers");
    }

    // Seen on a real server: every queue row read "456 S00E01". EpisodeKey.ToString()
    // carries the show id because a log line needs it to be unambiguous; a page does not,
    // because the show's name is the text right beside it.
    [Fact]
    public void Build_NamesTheEpisodeSlotWithoutLeakingTheShowId()
    {
        PluginView view = DownloadsView.Build(
            [Transfer("abc", 500, 1000)],
            [Grab("abc", 7, "Some.Show.S01E07.1080p")],
            [Wanted(2)]);

        List<string> text = TextOf(view);

        // The fixtures use show id 1, so the leak reads "1 S01E07".
        text.Should().Contain("S01E07").And.NotContain("1 S01E07");
        text.Should().Contain("S01E02").And.NotContain("1 S01E02");
    }

    [Fact]
    public void Build_AsksTheClientToComeBackBecauseTheseNumbersMove()
    {
        PluginView view = DownloadsView.Build(
            [Transfer("abc", 500, 1000)],
            [Grab("abc", 1, "Some.Show.S01E01.1080p")],
            []);

        // Zero means never, and a progress bar that only moves when the user reloads the
        // page is a screenshot. The ceiling is here because the other direction is just as
        // wrong: re-rendering every second costs a request per viewer for numbers the
        // transfers cadence only rewrites once a minute.
        view.RefreshInterval.Should().BeInRange(1, 60);
    }

    [Fact]
    public void Build_PutsTheFurthestAlongFirst()
    {
        PluginView view = DownloadsView.Build(
            [Transfer("slow", 100, 1000), Transfer("fast", 900, 1000)],
            [Grab("slow", 1, "Slow.Release"), Grab("fast", 2, "Fast.Release")],
            []);

        List<string> titles = [.. TextOf(view).Where(value => value.EndsWith(".Release"))];

        titles.Should().Equal("Fast.Release", "Slow.Release");
    }

    [Fact]
    public void Build_SaysNothingIsDownloadingRatherThanLookingBroken()
    {
        PluginView view = DownloadsView.Build([], [], [Wanted(1)]);

        // Idle is the common case. A page that looks like an error when nothing is
        // happening trains a user to ignore it.
        TextOf(view).Should().Contain("Nothing is downloading right now.");
    }

    [Fact]
    public void Build_SaysSoWhenTheLibraryIsComplete()
    {
        PluginView view = DownloadsView.Build([], [], []);

        TextOf(view).Should().Contain("Nothing is missing");
    }

    [Fact]
    public void Build_CountsTheQueueAndAdmitsWhenItIsTruncated()
    {
        PluginView view = DownloadsView.Build(
            [],
            [],
            [.. Enumerable.Range(1, 200).Select(number => Wanted(number))]);

        // A first run on a library with years of gaps wants hundreds. Rendering all of
        // them is a page nobody can read, and pretending there are 25 is a lie.
        TextOf(view).Should().Contain($"Wanted ({DownloadsView.QueuePreviewLength} of 200)");
    }

    [Fact]
    public void Build_LabelsEachWantedEpisodeWithWhereItStands()
    {
        PluginView view = DownloadsView.Build(
            [],
            [],
            [Wanted(1), Wanted(2, WantedState.Grabbed), Wanted(3, WantedState.Unavailable)]);

        IEnumerable<string> text = TextOf(view);

        text.Should().Contain("Wanted");
        text.Should().Contain("Downloading");
        text.Should().Contain("Not found");
    }

    [Fact]
    public void Build_FallsBackToTheInfoHashWhenTheGrabIsMissing()
    {
        // A transfer whose grab row was pruned still has to render something. Showing the
        // hash beats showing an empty row that looks like a bug.
        PluginView view = DownloadsView.Build([Transfer("orphan-hash", 1, 2)], [], []);

        TextOf(view).Should().Contain("orphan-hash");
    }

    [Fact]
    public void Build_SaysStartingRatherThanZeroPercentBeforeTheSizeIsKnown()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 0, 0)], [], []);

        TextOf(view).Should().Contain("starting");
    }
}
