using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Writing down what a cycle decided.
/// </summary>
/// <remarks>
/// A cycle answered with a report and nothing ever wrote it anywhere, so the
/// Downloads page was empty while a torrent was running and the Skipped page
/// was empty however much had been refused. What a cycle decided is a fact
/// about an episode the moment the client has been handed something.
/// </remarks>
public class CycleRecordTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-cycle-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AGrabIsRecordedWithItsHashItsMagnetAndEveryEpisodeItCovers()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([Taken], []),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        StoredDownload stored = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(Hash, stored.InfoHash);
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", stored.ReleaseTitle);
        Assert.StartsWith("magnet:?xt=urn:btih:", stored.Magnet, StringComparison.Ordinal);
        Assert.Equal(2, stored.Covers.Count);
    }

    /// <remarks>
    /// A decision the client was never handed is not a fact about an episode.
    /// Recording one would have the Downloads page show a row for a torrent
    /// nothing is downloading, which is the page saying something untrue.
    /// </remarks>
    [Fact]
    public async Task ADecisionNothingWasHandedIsNotRecordedAsAGrab()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null }], []),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Every refusal, with the reason it was refused for. The Skipped page is
    /// opened the morning after an episode did not arrive, and a list held for
    /// the cycle would be gone by then.
    /// </remarks>
    [Fact]
    public async Task EveryRefusalIsRecordedWithItsReason()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([], [new(Episode, "Silo.S03E06.720p.WEB-DL", "1337x", "720p is below the 1080p rung")]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        SkippedRelease refused = Assert.Single(await grabs.SkippedAsync(CancellationToken.None));

        Assert.Equal("Silo.S03E06.720p.WEB-DL", refused.Title);
        Assert.Equal("1337x", refused.Source);
        Assert.Contains("1080p rung", refused.Reason, StringComparison.Ordinal);
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static readonly DateTimeOffset When = new(2026, 8, 19, 9, 30, 0, TimeSpan.Zero);

    private static EpisodeKey Episode => new(41, 3, 6);

    private static TrackedEpisode Tracked =>
        new(Episode, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing);

    private static EpisodeOutcome Taken =>
        new(Episode, "Silo.S03E06.1080p.WEB.H264-CAKES", "1337x", 240, true, "taken from 1337x")
        {
            InfoHash = Hash,
            Magnet = $"magnet:?xt=urn:btih:{Hash}",
            Covers = [Episode, new(41, 3, 7)],
        };

    private async Task<GrabRepository> Repository()
    {
        Database database = new(_folder);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // The pool holds the file open, so it cannot be deleted until every
        // connection this test opened has really gone.

        TemporaryFolder.Forget(_folder);
    }
}
