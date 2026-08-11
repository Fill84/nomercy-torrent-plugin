// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>Only what is moving, and the buttons that change it.</summary>
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

    private static List<string> TextOf(PluginView view) => [.. PluginNodes.Words(view)];

    [Fact]
    public void Build_DrawsOnlyTagsAClientKnows()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [Grab("abc", 1, "Some.Show.S01E01.1080p")]);

        // A tag the client does not know renders as an "unsupported component" notice, which
        // is a page of apologies rather than a page.
        PluginNodes.All(view).Select(node => node.Component)
            .Should().OnlyContain(component => PluginNodes.KnownComponents.Contains(component!));
    }

    [Fact]
    public void Build_CarriesTheTabBar()
    {
        PluginNodes.All(DownloadsView.Build([], [])).Should().Contain(node => node.Id == "tab-queue");
    }

    [Fact]
    public void Build_ShowsWhatIsDownloadingAndHowFar()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [Grab("abc", 1, "Some.Show.S01E01.1080p")]);

        // The release is the block's heading; how far, how fast and with how many is the one
        // line under it, so these are read as words on the page rather than as whole nodes.
        TextOf(view).Should().Contain("Some.Show.S01E01.1080p");
        PluginNodes.Says(view, "50%").Should().BeTrue();
        PluginNodes.Says(view, "8 peers").Should().BeTrue();
    }

    // Seen on a real server: every row read "456 S00E01". EpisodeKey.ToString() carries the
    // show id because a log line needs it to be unambiguous; a page does not, because the
    // show's name is the text right beside it.
    [Fact]
    public void Build_NamesTheEpisodeSlotWithoutLeakingTheShowId()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [Grab("abc", 7, "Some.Show.S01E07.1080p")]);

        // The fixtures use show id 1, so the leak reads "1 S01E07".
        PluginNodes.Says(view, "S01E07").Should().BeTrue();
        PluginNodes.Says(view, "1 S01E07").Should().BeFalse();
    }

    // A pack row labelled with the single episode that triggered it reads as one episode
    // arriving, which is wrong about both the bytes and the wait.
    [Fact]
    public void Build_SaysHowManyEpisodesAPackIsBringing()
    {
        Grab pack = Grab("abc", 1, "Some.Show.S01.1080p") with
        {
            Covers =
            [
                new EpisodeKey(1, 1, 1),
                new EpisodeKey(1, 1, 2),
                new EpisodeKey(1, 1, 3),
            ],
        };

        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [pack]);

        PluginNodes.Says(view, "3 episodes").Should().BeTrue();
        PluginNodes.Says(view, "S01E01").Should().BeFalse();
    }

    /// <summary>
    /// The shape this page was rebuilt for.
    ///
    /// <para>
    /// A progress bar takes the full width of whatever holds it, so a wrapping row that
    /// contained one re-flowed around it: the title on one line, the rate below, the buttons
    /// on a third, and nothing lining up with the download above. A block puts the name, the
    /// line about where it stands, the bar and the buttons in that order every time.
    /// </para>
    /// </summary>
    [Fact]
    public void Build_DrawsEachDownloadAsABlockRatherThanAWrappingRow()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [Grab("abc", 1, "Some.Show.S01E01.1080p")]);

        PluginComponent block = PluginNodes.All(view).Should()
            .ContainSingle(node => node.Id == "downloads-row-abc").Which;

        block.Component.Should().Be(Ui.DetailComponent);
        block.Props["title"].Should().Be("Some.Show.S01E01.1080p");
        block.Items.Should().Contain(child => child.Component == Ui.ProgressComponent);
    }

    [Fact]
    public void Build_AsksTheClientToComeBackBecauseTheseNumbersMove()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 500, 1000)], [Grab("abc", 1, "Some.Show.S01E01.1080p")]);

        // Zero means never, and a progress bar that only moves when the user reloads the page
        // is a screenshot. The ceiling is here because the other direction is just as wrong:
        // re-rendering every second costs a request per viewer for numbers the transfers
        // cadence only rewrites once a minute.
        view.RefreshInterval.Should().BeInRange(1, 60);
    }

    [Fact]
    public void Build_PutsTheFurthestAlongFirst()
    {
        PluginView view = DownloadsView.Build(
            [Transfer("slow", 100, 1000), Transfer("fast", 900, 1000)],
            [Grab("slow", 1, "Slow.Release"), Grab("fast", 2, "Fast.Release")]);

        List<string> titles = [.. TextOf(view).Where(value => value.EndsWith(".Release"))];

        titles.Should().Equal("Fast.Release", "Slow.Release");
    }

    [Fact]
    public void Build_SaysNothingIsDownloadingRatherThanLookingBroken()
    {
        PluginView view = DownloadsView.Build([], []);

        // Idle is the common case. A page that looks like an error when nothing is happening
        // trains a user to ignore it.
        TextOf(view).Should().Contain("Nothing is downloading right now.");
    }

    [Fact]
    public void Build_FallsBackToTheInfoHashWhenTheGrabIsMissing()
    {
        // A transfer whose grab row was pruned still has to render something. Showing the
        // hash beats showing an empty row that looks like a bug.
        PluginView view = DownloadsView.Build([Transfer("orphan-hash", 1, 2)], []);

        TextOf(view).Should().Contain("orphan-hash");
    }

    [Fact]
    public void Build_SaysStartingRatherThanZeroPercentBeforeTheSizeIsKnown()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 0, 0)], []);

        PluginNodes.Says(view, "starting").Should().BeTrue();
    }

    // The escape hatch stays on this page: a magnet found by hand is about a download, and
    // this is the page about downloads.
    [Fact]
    public void Build_TakesAMagnetByHand()
    {
        PluginComponent form = PluginNodes.All(DownloadsView.Build([], [])).Should()
            .ContainSingle(node => node.Id == "downloads-add-form").Which;

        form.Action!.Payload["method"].Should().Be("AddTorrent");
    }

    // Cancelling deletes the bytes and skips the release for a fortnight. That is not an undo
    // away, so it asks first.
    [Fact]
    public void Build_AsksBeforeCancelling()
    {
        PluginComponent cancel = PluginNodes.All(DownloadsView.Build([Transfer("abc", 1, 2)], []))
            .Should().ContainSingle(node => node.Id == "downloads-cancel-abc").Which;

        cancel.Action!.Confirm.Should().NotBeNull();
        cancel.Action.Payload["method"].Should().Be("CancelDownload/abc");
    }

    [Fact]
    public void Build_OffersResumeRatherThanPauseOnAPausedDownload()
    {
        PluginView view = DownloadsView.Build([Transfer("abc", 1, 2) with { Paused = true }], []);

        PluginNodes.All(view).Should().Contain(node => node.Id == "downloads-resume-abc");
        PluginNodes.All(view).Should().NotContain(node => node.Id == "downloads-pause-abc");
    }

    /// <summary>
    /// No percentage and no rate until a peer says how big the torrent is. "0%" reads as a
    /// download that has stalled, which is a different thing to worry about from one that
    /// has not begun.
    /// </summary>
    [Fact]
    public void Build_SaysWhenATorrentIsStillLookingForPeers()
    {
        PluginView view = DownloadsView.Build(
            [new Transfer { InfoHash = "abc" }],
            [Grab("abc", 1, "Some.Show.S01E01") with { State = GrabState.Resolving }]);

        PluginNodes.Says(view, "Finding peers").Should().BeTrue();
        PluginNodes.Says(view, "0%").Should().BeFalse();
    }
}
