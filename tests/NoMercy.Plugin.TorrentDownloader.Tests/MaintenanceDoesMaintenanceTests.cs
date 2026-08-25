using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
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
    /// One refusal is written for every release every cycle considered and did
    /// not take: the owner's history held 66,149 lines, 65,878 of them
    /// refusals, and the page stopped answering. A fortnight is long enough to
    /// look back at why something did not arrive.
    /// </remarks>
    [Fact]
    public async Task TheMaintenanceCadenceClearsRefusalsNobodyWillReadAgain()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        await Refused(grabs, "Silo.S03E06.2160p.WEB.H265-OLD", DateTimeOffset.UtcNow.AddDays(-30));
        await Refused(grabs, "Silo.S03E06.2160p.WEB.H265-NEW", DateTimeOffset.UtcNow.AddDays(-1));

        await plugin.ExecuteAsync(JobNames.Maintenance, CancellationToken.None);

        SkippedRelease left = Assert.Single((await grabs.SkippedAsync(1, 50, CancellationToken.None)).Rows);

        Assert.Equal("Silo.S03E06.2160p.WEB.H265-NEW", left.Title);
    }

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
    /// not the tick, so the start is where it is done — and the refusal pruned
    /// here can have been pruned by nothing else, because a search cycle does
    /// no housekeeping of its own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStartSettlesOnItsFirstTickWhateverTheCadence()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        // A refusal old enough to be pruned, and one that is not. Pruning is
        // housekeeping the maintenance cadence owes, so a search cadence
        // clearing it is the start settling rather than the cadence doing
        // somebody else's work.
        await Refused(grabs, "Silo.S03E06.2160p.WEB.H265-OLD", DateTimeOffset.UtcNow.AddDays(-30));
        await Refused(grabs, "Silo.S03E06.2160p.WEB.H265-NEW", DateTimeOffset.UtcNow.AddDays(-1));

        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        SkippedRelease left = Assert.Single((await grabs.SkippedAsync(1, 50, CancellationToken.None)).Rows);

        Assert.Equal("Silo.S03E06.2160p.WEB.H265-NEW", left.Title);
    }

    private static async Task Grabbed(GrabRepository grabs, string hash)
    {
        await grabs.RecordAsync(
            new EpisodeKey(41, 3, 6),
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "1337x",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            [new EpisodeKey(41, 3, 6)],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private static async Task Refused(GrabRepository grabs, string release, DateTimeOffset at)
    {
        await grabs.RecordSkippedAsync(
            new EpisodeKey(41, 3, 6),
            "Silo",
            release,
            "1337x",
            "h265 is not allowed by the profile",
            at,
            CancellationToken.None);
    }

    /// <summary>
    /// A plugin with somewhere to put things and a library to read, in dry run
    /// so that no cycle reaches for a network.
    /// </summary>
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
                DryRun = true,
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
