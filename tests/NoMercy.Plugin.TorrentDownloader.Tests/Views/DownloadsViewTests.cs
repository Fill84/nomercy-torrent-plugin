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

        // Nine seeds, and twenty-four connections of which nine are seeds, so
        // fifteen leechers. The peers column counts leechers: this used to
        // assert "24" against a column headed with the swarm's leechers, which
        // is two populations in one place.
        Assert.Contains(Rendered.EveryValue(view), one => one == "15");
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

        Assert.Contains("fetching metadata", row, StringComparison.Ordinal);
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
    /// A state made of two words is read as two words. The state is the enum's
    /// own name lower-cased, which turns <c>FetchingMetadata</c> into
    /// "fetchingmetadata" on the owner's page — a word that is not a word, on
    /// the column an owner reads first to know what a download is doing.
    /// </remarks>
    [Fact]
    public void AStateOfTwoWordsIsReadableAsTwoWords()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    null,
                    TorrentState.FetchingMetadata,
                    0,
                    null,
                    0,
                    0,
                    0,
                    0,
                    null,
                    null,
                    null),
                @"D:\incomplete"),
        ]);

        string drawn = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("fetching metadata", drawn, StringComparison.Ordinal);
        Assert.DoesNotContain("fetchingmetadata", drawn, StringComparison.Ordinal);
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

    /// <remarks>
    /// <para>
    /// <strong>Nought per cent is not nothing arriving.</strong> A piece counts
    /// when it is whole and hashes right, and a piece here is eight mebibytes.
    /// Off a swarm giving four kilobytes a second that is half an hour per
    /// piece, and the blocks come in spread across several at once — so a
    /// torrent can take megabytes for hours and still be at nought verified,
    /// truthfully.
    /// </para>
    /// <para>
    /// The owner read <c>0% · 0 B/s</c> against Rings of Power S02E06 on
    /// 3 September 2026 and asked, twice, why it was not downloading. It was:
    /// 8.7 MB had arrived. The page knew — it is in the session as
    /// <c>Downloaded</c> — and drew the one number that says nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATorrentWithNothingVerifiedYetStillSaysWhatHasArrived()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    "Silo S03E06 1080p",
                    TorrentState.Downloading,
                    BytesDone: 0,
                    BytesTotal: 2_870_000_000,
                    DownloadRateBytesPerSecond: 0,
                    UploadRateBytesPerSecond: 0,
                    Peers: 1,
                    Seeds: 1,
                    Ratio: 0,
                    Eta: null,
                    Error: null,
                    Arrived: 8_700_000),
                "D:\\incomplete"),
        ]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("0% of 2.7 GB", row, StringComparison.Ordinal);
        Assert.Contains("8.3 MB in", row, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And it is said only where it says something. A torrent whose bytes are
    /// verified as fast as they land has nothing to add, and a second figure
    /// beside the first in every row is clutter that makes the one row that
    /// needs it harder to see.
    /// </remarks>
    [Fact]
    public void ATorrentVerifyingEverythingThatArrivesDoesNotSayItTwice()
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
                    UploadRateBytesPerSecond: 0,
                    Peers: 24,
                    Seeds: 9,
                    Ratio: 0.35,
                    Eta: null,
                    Error: null,
                    Arrived: 2_000_000_000),
                "D:\\incomplete"),
        ]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("50% of 3.7 GB", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" in)", row, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And never on a torrent that is finished. The clause is there so nought
    /// per cent does not read as nothing happening; on a complete torrent it
    /// answers a question nobody has, and it appears at all only because
    /// re-requested blocks make what arrived a few bytes larger than what is
    /// verified. The owner read "100% of 2.7 GB (2.7 GB in)" and asked what was
    /// going wrong, which is the fairest possible response to it.
    /// </remarks>
    [Fact]
    public void AFinishedTorrentDoesNotSayWhatArrivedBesideWhatIsVerified()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    "Silo S03E06 1080p",
                    TorrentState.Finished,
                    BytesDone: 2_870_000_000,
                    BytesTotal: 2_870_000_000,
                    DownloadRateBytesPerSecond: 0,
                    UploadRateBytesPerSecond: 0,
                    Peers: 0,
                    Seeds: 0,
                    Ratio: 0,
                    Eta: null,
                    Error: null,

                    // A handful of bytes more than the file, which is what
                    // re-requesting a block that failed its hash costs.
                    Arrived: 2_870_004_096),
                @"D:\incomplete"),
        ]);

        string row = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("100% of 2.7 GB", row, StringComparison.Ordinal);
        Assert.DoesNotContain(" in)", row, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>Peers are leechers. Seeds have all of it. The two columns count
    /// two populations and the page mixed them.</strong> A tracker answers with
    /// <c>seeders</c> and <c>leechers</c> and they are disjoint — nobody is
    /// both. The right-hand half of the peers column was the swarm's leechers,
    /// and the left-hand half was every connection this client had, seeds
    /// included. So "5 of 8" could mean five connections of which three were
    /// seeds, against eight leechers: two numbers that cannot be compared,
    /// printed as though they could.
    /// </para>
    /// <para>
    /// The owner had to say it more than once, and they were right every time.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePeersColumnCountsLeechersAndTheSeedsColumnCountsSeeds()
    {
        PluginView view = DownloadsView.Render(
        [
            new(
                Grab(),
                new(
                    Hash,
                    "Silo S03E06 1080p",
                    TorrentState.Downloading,
                    BytesDone: 1_000_000_000,
                    BytesTotal: 4_000_000_000,
                    DownloadRateBytesPerSecond: 0,
                    UploadRateBytesPerSecond: 0,

                    // Eleven connections, three of which have the lot. So eight
                    // leechers are connected, not eleven.
                    Peers: 11,
                    Seeds: 3,
                    Ratio: 0,
                    Eta: null,
                    Error: null,
                    SwarmSeeds: 52,
                    SwarmPeers: 110),
                @"D:\incomplete"),
        ]);

        IReadOnlyList<string> cells = [.. Rendered.EveryValue(view)];

        Assert.Contains("3 of 52", cells);
        Assert.Contains("8 of 110", cells);

        // And never the connection count, which is neither of the two things
        // the columns are headed with.
        Assert.DoesNotContain("11 of 110", cells);
    }
}
