using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The cadence named for the housekeeping is the one that does it.
/// </summary>
/// <remarks>
/// <para>
/// Maintenance runs at four in the morning and its whole body used to be a
/// refresh that the search cadence already did before each of its four daily
/// cycles. The real periodic work was elsewhere: old refusals were pruned as a
/// side effect of that refresh, and duplicate grab rows were cleared on the
/// first transfers tick after a start, behind a flag.
/// </para>
/// <para>
/// Three pieces of periodic housekeeping, none of them in the cadence named for
/// it, and one cadence whose whole body was a duplicate.
/// </para>
/// </remarks>
public class MaintenanceDoesMaintenanceTests : IDisposable
{
    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private const string Other = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-maintenance-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <para>
    /// <strong>A start settles once, whichever cadence ticks first.</strong>
    /// What the library holds is derived rather than stored, and a plugin that
    /// only re-derived it on its six-hourly cycle carried whatever the last run
    /// left behind — including, on 24 August 2026, shows a broken build had put
    /// there that the owner does not have. A restart settles that within the
    /// minute rather than by tea time.
    /// </para>
    /// <para>
    /// It used to be the first transfers tick that did this, which made one
    /// tick of one cadence unlike all the others. What is special is the start,
    /// not the tick, so the start is where it is done.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStartSettlesOnItsFirstTickWhateverTheCadence()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        EpisodeRepository episodes = await plugin.EpisodesAsync(CancellationToken.None);

        // A row a broken build left behind: an episode of a show this library
        // does not have. Only a re-derivation from the library takes it out, and
        // a search cycle does no housekeeping of its own.
        await episodes.ReplaceAsync(
            [
                new(
                    new EpisodeKey(999, 1, 1),
                    "A show nobody has",
                    null,
                    LibraryKind.Television,
                    null,
                    new DateOnly(2020, 1, 1),
                    EpisodeState.Missing),
            ],
            CancellationToken.None);

        await plugin.RunCycleAsync(CancellationToken.None);

        Assert.DoesNotContain(
            await episodes.AllAsync(CancellationToken.None),
            episode => episode.Key.ShowId == 999);
    }

    /// <summary>A plugin with somewhere to put things and a library to read.</summary>
    private async Task<TorrentDownloaderPlugin> Configured()
    {
        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2021, folder: "/Silo.(2021)")
            .Episode(41, 3, 5, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 3, 6, "Under Pressure", new DateTime(2020, 1, 8), hasFile: false)
            .Episode(41, 3, 7, "Descent", new DateTime(2020, 1, 15), hasFile: true);

        Directory.CreateDirectory(_folder);

        TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = shelves,
        });

        await plugin.Settings.SaveAsync(
            new Settings
            {
                IncompleteFolder = _folder,
                IntakeFolder = _folder,
            },
            CancellationToken.None);

        return plugin;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        TemporaryFolder.Forget(_folder);
    }
}
