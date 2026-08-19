using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// What has to keep being true when things go wrong.
/// </summary>
/// <remarks>
/// Stopping, restarting and shutting down: three moments 0.3.4 got wrong in
/// three different ways, and all three are cheap to get wrong again.
/// </remarks>
public class HardeningTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-hard-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Stopping a search is not stopping a download. The owner presses Stop
    /// because the plugin is asking sites for things they do not want; the
    /// eight gigabytes already coming down are not what they meant, and a
    /// client that dropped them would lose hours of transfer to a button about
    /// something else.
    /// </remarks>
    [Fact]
    public async Task StoppingTheCycleLeavesTheTransfersAlone()
    {
        using TorrentDownloaderPlugin plugin = Initialised();

        await Configure(plugin);

        (string? InfoHash, string? Refusal) added = await plugin.AddTorrentAsync(
            $"magnet:?xt=urn:btih:{Hash}&dn=Silo+S03E06",
            CancellationToken.None);

        Assert.Equal(Hash, added.InfoHash);

        plugin.StopRun();

        StoredDownload still = Assert.Single(
            await (await plugin.GrabsAsync(CancellationToken.None)).OpenAsync(CancellationToken.None));

        Assert.Equal(Hash, still.InfoHash);
    }

    /// <remarks>
    /// <para>
    /// A restart mid-cycle must not grab again what it has already grabbed. The
    /// episode was marked unavailable when it was taken, so the missing list
    /// the next cycle works from does not have it — and the grab is still in
    /// the store for recovery to re-add rather than re-download.
    /// </para>
    /// <para>
    /// Nor re-harvest: the names are in the pool, written before anything read
    /// them, so the feeds are not asked all over again for what is already
    /// known.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARestartMidCycleDoesNotGrabAgainOrHarvestAgain()
    {
        using (TorrentDownloaderPlugin before = Initialised())
        {
            await Configure(before);

            await (await before.EpisodesAsync(CancellationToken.None)).ReplaceAsync(
                [
                    new(Taken, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Unavailable),
                    new(Waiting, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing),
                ],
                CancellationToken.None);

            await (await before.GrabsAsync(CancellationToken.None)).RecordAsync(
                Taken,
                "Silo",
                "Silo.S03E06.1080p.WEB.H264-CAKES",
                "1337x",
                Hash,
                $"magnet:?xt=urn:btih:{Hash}",
                [Taken],
                DateTimeOffset.UtcNow,
                CancellationToken.None);

            await new NamePoolRepository(new Database(_folder)).AddAsync(
                [new("silos03e06", "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", DateTimeOffset.UtcNow)],
                CancellationToken.None);
        }

        // A different plugin over the same folder, which is what a restart is.
        using TorrentDownloaderPlugin after = Initialised();

        IReadOnlyList<TrackedEpisode> tracked =
            await (await after.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

        // The one it took is not waiting to be looked for again.
        Assert.Equal(EpisodeState.Unavailable, tracked.Single(one => one.Key == Taken).State);
        Assert.Equal(EpisodeState.Missing, tracked.Single(one => one.Key == Waiting).State);

        // And the grab is still there for recovery to re-add rather than
        // download all over again.
        Assert.Single(await (await after.GrabsAsync(CancellationToken.None)).OpenAsync(CancellationToken.None));

        // And the names it harvested are still known.
        Assert.NotEmpty(await new NamePoolRepository(new Database(_folder))
            .ForAsync(["silos03e06"], CancellationToken.None));
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static EpisodeKey Taken => new(41, 3, 6);

    private static EpisodeKey Waiting => new(41, 3, 7);

    private TorrentDownloaderPlugin Initialised(FakeGrants? grants = null)
    {
        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder, Permits = grants });

        return plugin;
    }

    private async Task Configure(TorrentDownloaderPlugin plugin)
    {
        await plugin.Settings.SaveAsync(
            new()
            {
                IncompleteFolder = Path.Combine(_folder, "incomplete"),
                IntakeFolder = Path.Combine(_folder, "intake"),
            },
            CancellationToken.None);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
