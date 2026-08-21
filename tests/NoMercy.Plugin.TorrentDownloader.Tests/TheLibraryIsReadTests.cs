using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The plugin reads the owner's library and writes down what is missing.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole point of the plugin and nothing drove it.
/// <c>MissingRefresh</c> derives the list and
/// <c>EpisodeRepository.ReplaceAsync</c> stores it; both were written, both
/// were tested on their own, and <em>neither had a caller anywhere</em>. The
/// maintenance cadence was a <c>default: break;</c> with a comment saying a
/// library refresh was somebody else's slice.
/// </para>
/// <para>
/// So on a real server every page said "Nothing is outstanding. Every episode
/// of every show is on disk." over a library of 67 shows with 1,973 episodes
/// that have no file, and every cycle decided nothing because it had nothing to
/// decide about.
/// </para>
/// </remarks>
public class TheLibraryIsReadTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-library-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task TheMaintenanceCadenceWritesDownEveryEpisodeWithNoFile()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        await plugin.ExecuteAsync(JobNames.Maintenance, CancellationToken.None);

        IReadOnlyList<TrackedEpisode> tracked =
            await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

        Assert.Equal(2, tracked.Count);
        Assert.All(tracked, episode => Assert.Equal("Silo", episode.ShowTitle));
    }

    /// <remarks>
    /// A cycle that ran before the library had ever been read decided nothing,
    /// which on a fresh install is every cycle there has ever been. Pressing
    /// Run now has to be enough on its own.
    /// </remarks>
    [Fact]
    public async Task ACycleReadsTheLibraryBeforeItDecidesAnything()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        await plugin.ExecuteAsync(JobNames.Search, CancellationToken.None);

        Assert.NotEmpty(
            await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None));
    }

    /// <remarks>
    /// A film library is not this plugin's business, and neither is one whose
    /// type it does not know. Taking them would have it downloading into a
    /// library nobody meant for this.
    /// </remarks>
    [Fact]
    public async Task OnlyTelevisionAndAnimeLibrariesAreRead()
    {
        using TorrentDownloaderPlugin plugin = await Configured();

        await plugin.ExecuteAsync(JobNames.Maintenance, CancellationToken.None);

        IReadOnlyList<TrackedEpisode> tracked =
            await (await plugin.EpisodesAsync(CancellationToken.None)).AllAsync(CancellationToken.None);

        Assert.DoesNotContain(tracked, episode => episode.ShowTitle == "Dune");
    }

    private async Task<TorrentDownloaderPlugin> Configured()
    {
        FakeLibraryQuery shelves = new();

        shelves
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Library("01HQ5W44HBTYWCGGBSRVR2ZXHN", "Films", "movie")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2021, folder: "/Silo.(2021)")
            .Show(99, "Dune", "01HQ5W44HBTYWCGGBSRVR2ZXHN", 2021, folder: "/Dune.(2021)")

            // Aired, no file: exactly what the plugin exists to fill in.
            .Episode(41, 3, 5, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 3, 6, "Under Pressure", new DateTime(2020, 1, 8), hasFile: false)

            // On disk, so nothing to do about it.
            .Episode(41, 3, 7, "Descent", new DateTime(2020, 1, 15), hasFile: true)
            .Episode(99, 1, 1, "Arrakis", new DateTime(2020, 1, 1), hasFile: false);

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
        TemporaryFolder.Forget(_folder);
        GC.SuppressFinalize(this);
    }
}
