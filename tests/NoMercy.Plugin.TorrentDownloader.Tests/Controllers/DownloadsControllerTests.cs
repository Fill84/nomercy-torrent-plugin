using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

/// <summary>
/// The controls on the Downloads and Skipped pages.
/// </summary>
/// <remarks>
/// Every one of these is a button an owner presses when something has gone
/// wrong: a download that will not finish, a release the profile refused that
/// they can see is the right one, a torrent they found themselves. An endpoint
/// that answered "ok" to having done nothing would leave them pressing it
/// again.
/// </remarks>
public class DownloadsControllerTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-actions-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Cancelling stops the torrent, forgets it, and puts every episode it
    /// answered for back to missing — all three, or the episode is lost. It is
    /// not blacklisted: the owner cancelled it, and a release they may want to
    /// choose again tomorrow is not one the plugin should refuse for them.
    /// </remarks>
    [Fact]
    public async Task CancellingADownloadRemovesItAndReturnsItsEpisodesToMissing()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        await Configure(plugin);
        await Grabbed(plugin);

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Cancel(Hash, CancellationToken.None));

        Assert.Equal("cancelled", Assert.IsType<PluginStatusResponse<bool>>(result.Value).Status);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // Not blacklisted. The owner said no to this download, not to this
        // release for ever.
        Assert.DoesNotContain(Hash, await grabs.BlacklistedAsync(CancellationToken.None));

        TrackedEpisode episode = Assert.Single(
            await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None));

        Assert.Equal(EpisodeState.Missing, episode.State);
    }

    /// <remarks>
    /// A hash this client is not holding is refused rather than answered with
    /// "ok". A page that showed a torrent pausing when nothing paused is a page
    /// nobody can trust about anything else either.
    /// </remarks>
    [Fact]
    public async Task PausingSomethingThisClientIsNotHoldingSaysSo()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        await Configure(plugin);

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Pause(Hash, CancellationToken.None));
        PluginStatusResponse<bool> response = Assert.IsType<PluginStatusResponse<bool>>(result.Value);

        Assert.Equal("unknown", response.Status);
        Assert.False(response.Data);
        Assert.Contains(Hash, response.Message!, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A source the client will not take is refused with the client's own
    /// words. "Could not add torrent" tells the owner nothing they can act on;
    /// naming what they pasted tells them which of the three things they tried
    /// was wrong.
    /// </remarks>
    [Fact]
    public async Task ATorrentAddedByHandThatIsNeitherAMagnetNorATorrentIsRefusedWithTheReason()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        await Configure(plugin);

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Add(new("ftp://example.test/thing.bin"), CancellationToken.None));
        PluginStatusResponse<string?> response = Assert.IsType<PluginStatusResponse<string?>>(result.Value);

        Assert.Equal("refused", response.Status);
        Assert.Contains("thing.bin", response.Message!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A magnet added by hand is a grab like any other: it is written down, so
    /// the Downloads page shows it and it is staged the moment it finishes. One that was taken and never recorded is a file that arrives
    /// and is never put anywhere.
    /// </remarks>
    [Fact]
    public async Task AMagnetAddedByHandIsTakenAndWrittenDown()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        await Configure(plugin);

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Add(new($"magnet:?xt=urn:btih:{Hash}&dn=Silo+S03E06"), CancellationToken.None));
        PluginStatusResponse<string?> response = Assert.IsType<PluginStatusResponse<string?>>(result.Value);

        Assert.Equal("added", response.Status);
        Assert.Equal(Hash, response.Data);

        StoredDownload stored = Assert.Single(
            await (await plugin.GrabsAsync(CancellationToken.None)).OpenAsync(CancellationToken.None));

        Assert.Equal(Hash, stored.InfoHash);
    }

    /// <remarks>
    /// <para>
    /// Searching one episode now is the button an owner presses when they can
    /// see something has just aired and do not want to wait six hours for the
    /// cadence. It answers at once and the search runs on the plugin's own
    /// lifetime, for the same reason <c>RunNow</c> does (**F1**).
    /// </para>
    /// <para>
    /// An episode this plugin is not tracking is refused rather than answered
    /// "started": a page that showed a search beginning for something it has
    /// never heard of is one nobody can trust about the rest of the queue.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task SearchingAnEpisodeThisPluginIsNotTrackingSaysSo()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        // Or the refusal below could be the one an open cycle gives, and this would
        // pass whether or not an unknown episode is refused at all.
        await JustRan();
        await Configure(plugin);

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Search(new(41, 3, 6), CancellationToken.None));
        PluginStatusResponse<bool> response = Assert.IsType<PluginStatusResponse<bool>>(result.Value);

        Assert.Equal("unknown", response.Status);
        Assert.False(response.Data);
        Assert.Contains("S03E06", response.Message!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// One it is tracking is started, and the request's own cancellation has
    /// nothing to do with it: the search belongs to the plugin.
    /// </remarks>
    [Fact]
    public async Task SearchingAnEpisodeItIsTrackingStartsAndDoesNotBelongToTheCaller()
    {
        using TorrentDownloaderPlugin plugin = Initialised(out FakePluginContext context);

        // A search is refused while a cycle is open, and a plugin that has never run
        // opens one the moment the folders are saved. This test is about the button.
        await JustRan();
        await Configure(plugin);
        await (await plugin.EpisodesAsync(CancellationToken.None)).ReplaceAsync(
            [
                new(Episode, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing),
            ],
            CancellationToken.None);

        using CancellationTokenSource gone = new();

        await gone.CancelAsync();

        DownloadsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Search(new(41, 3, 6), gone.Token));

        Assert.Equal("started", Assert.IsType<PluginStatusResponse<bool>>(result.Value).Status);
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static EpisodeKey Episode => new(41, 3, 6);

    private TorrentDownloaderPlugin Initialised(out FakePluginContext context)
    {
        context = new() { DataFolderPath = _folder };

        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(context);

        return plugin;
    }

    /// <summary>Writes down that the cycle has just run, so none is due while this test presses a button.</summary>
    /// <remarks>
    /// <strong>Otherwise the plugin starts one for itself.</strong> <c>Clock.NextAsync</c> reads when
    /// the cycle last finished, and with no row that is <c>MinValue</c>, so the next one is already
    /// due: saving the folders winds the timer to nought and a cycle opens. <c>StartSearchAsync</c>
    /// refuses while one is open, so the Search test read "unknown" whenever the start won the race —
    /// measured on 22 September 2026. Written before the folders are saved, because that save is what
    /// winds it.
    /// </remarks>
    private async Task JustRan()
    {
        Store database = new(_folder);

        await database.MigrateAsync(CancellationToken.None);
        await new CadenceRepository(database).RecordFinishedAsync(
            Cadences.Name, DateTimeOffset.UtcNow, CancellationToken.None);
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

    /// <summary>One episode, missing, and one grab that answers for it.</summary>
    private static async Task Grabbed(TorrentDownloaderPlugin plugin)
    {
        await (await plugin.EpisodesAsync(CancellationToken.None)).ReplaceAsync(
            [
                new(Episode, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing),
            ],
            CancellationToken.None);

        await (await plugin.GrabsAsync(CancellationToken.None)).RecordAsync(
            Episode,
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        TemporaryFolder.Forget(_folder);
    }
    /// <summary>
    /// The controller as the host builds one: from the server's container, on a
    /// request, on the route that says which plugin was asked for.
    /// </summary>
    private static DownloadsController For(TorrentDownloaderPlugin plugin)
    {
        return new DownloadsController(new LoadedPlugins(plugin)).On(plugin.Id);
    }

}
