using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Views;

/// <summary>
/// The Downloads and History pages, rendered from a seeded store.
/// </summary>
public class DownloadsViewTests
{
    /// <remarks>
    /// <strong>G4.</strong> 0.3.4 built this page from the client's list alone,
    /// so a grab the client had not taken up yet — or had quietly lost — was on
    /// no page at all: the episode showed as unavailable, nothing was
    /// downloading, and there was nowhere to find out why. The row is here, and
    /// it says which of the two it is.
    /// </remarks>
    [Fact]
    public void AGrabWithNoTransferYetStillGetsARowThatSaysSo()
    {
        PluginView view = DownloadsView.Render([new(Grab(), null, "D:\\incomplete")]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("Silo S03E06 1080p", row, StringComparison.Ordinal);
        Assert.Contains("grabbed, not started", row, StringComparison.Ordinal);

        // And every number it cannot know says so rather than being drawn as
        // nought, which is the fault this whole page is shaped by. Peers and
        // seeds especially: "0 peers" is a torrent nobody is sharing, and this
        // one has not been asked yet.
        Assert.DoesNotContain("0%", row, StringComparison.Ordinal);
        Assert.DoesNotContain(Rendered.EveryValue(view), one => one == "0");

        // Three of them — peers, seeds and ratio — plus progress and rate.
        Assert.InRange(Rendered.EveryValue(view).Count(one => one == "—"), 3, 5);
    }

    /// <remarks>
    /// Everything the page is for, from a store: progress, rate, peers, seeds,
    /// ratio and where the bytes are landing.
    /// </remarks>
    [Fact]
    public void ProgressRatePeersSeedsRatioAndDestinationAllRenderFromTheStore()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    "Silo S03E06 1080p",
                    TorrentState.Downloading,
                    BytesDone: 2_000_000_000,
                    BytesTotal: 4_000_000_000,
                    DownloadRateBytesPerSecond: 5 * 1024 * 1024,
                    UploadRateBytesPerSecond: 512 * 1024,
                    Peers: 24,
                    Seeds: 9,
                    Ratio: 0.35,
                    Eta: TimeSpan.FromMinutes(7),
                    Error: null),
                "D:\\incomplete"),
        ]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("50% of 3.7 GB", row, StringComparison.Ordinal);
        Assert.Contains("5 MB/s", row, StringComparison.Ordinal);
        Assert.Contains("512 KB/s", row, StringComparison.Ordinal);
        Assert.Contains("0.35", row, StringComparison.Ordinal);
        Assert.Contains("D:\\incomplete", row, StringComparison.Ordinal);

        Assert.Contains(Rendered.EveryValue(view), one => one == "24");
        Assert.Contains(Rendered.EveryValue(view), one => one == "9");
    }

    /// <remarks>
    /// A magnet still fetching its metadata has no size at all, and a
    /// percentage of a size nobody knows is a number invented on the page.
    /// </remarks>
    [Fact]
    public void ATorrentWhoseSizeNobodyKnowsShowsNoPercentage()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(Hash, null, TorrentState.FetchingMetadata, 0, null, 0, 0, 0, 0, null, null, null),
                "D:\\incomplete"),
        ]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("fetchingmetadata", row, StringComparison.Ordinal);
        Assert.DoesNotContain("%", row, StringComparison.Ordinal);

        // And a size reported as nought is the same case: dividing by it gives
        // a percentage that is not a number, and the page would print it.
        PluginView empty = DownloadsView.Render(
        [
            new(
                Grab(),
                new(Hash, null, TorrentState.Checking, 0, 0, 0, 0, 0, 0, null, null, null),
                "D:\\incomplete"),
        ]);

        Assert.DoesNotContain("%", string.Join(" ", Rendered.EveryValue(empty)), StringComparison.Ordinal);
    }

    /// <remarks>
    /// A torrent in error says what went wrong on the row itself. An owner
    /// looking at a stopped download should not have to open the journal to
    /// find out why it stopped.
    /// </remarks>
    [Fact]
    public void ATorrentInErrorCarriesItsReasonOnTheRow()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    null,
                    TorrentState.Error,
                    0,
                    null,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    "No peer sent its metadata within 5 minutes."),
                "D:\\incomplete"),
        ]);

        Assert.Contains(
            "No peer sent its metadata within 5 minutes.",
            string.Join(" ", Rendered.EveryValue(view)),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// Nothing grabbed is a page that says so, rather than an empty table with
    /// no explanation.
    /// </remarks>
    [Fact]
    public void AnEmptyPageSaysNothingHasBeenGrabbed()
    {
        Assert.Contains(
            "Nothing has been grabbed.",
            string.Join(" ", Rendered.EveryValue(DownloadsView.Render([]))),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// Every kind of line, each with what it carries: skipped and failed with
    /// their reasons, dispatched with the library it went to. Those reasons are
    /// what an owner opens this page for.
    /// </remarks>
    [Fact]
    public void HistoryShowsEveryKindOfLineWithItsOwnReason()
    {
        PluginView view = HistoryView.Render(
        [
            new("grabbed", At, "Silo S03E06", "from LimeTorrents"),
            new("skipped", At, "Silo S03E06 720p", "resolution 720p is below the profile's floor"),
            new("failed", At, "Silo S03E06 1080p", "No peer sent its metadata within 5 minutes."),
            new("dispatched", At, "Silo S03E06", "encode dispatched to library library-tv"),
            new("allowed", At, "Silo S03E06 720p", "allowed by hand, was: below the profile's floor"),
        ]);

        string page = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("grabbed", page, StringComparison.Ordinal);
        Assert.Contains("below the profile's floor", page, StringComparison.Ordinal);
        Assert.Contains("No peer sent its metadata within 5 minutes.", page, StringComparison.Ordinal);
        Assert.Contains("library-tv", page, StringComparison.Ordinal);
        Assert.Contains("allowed by hand", page, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A line with no reason still renders something rather than a blank cell:
    /// a blank is indistinguishable from a page that failed to load.
    /// </remarks>
    [Fact]
    public void AHistoryLineWithNoReasonIsNotABlankCell()
    {
        PluginView view = HistoryView.Render([new("grabbed", At, "Silo S03E06", null)]);

        Assert.DoesNotContain(Rendered.EveryValue(view), string.IsNullOrWhiteSpace);
    }

    private const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

    private static DateTimeOffset At => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    private static StoredDownload Grab()
    {
        return new(Hash, $"magnet:?xt=urn:btih:{Hash}", "Silo S03E06 1080p", GrabState.Grabbed);
    }
}
