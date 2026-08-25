using Microsoft.Extensions.Time.Testing;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// Which shows are the owner's is one rule, and both halves of the plugin
/// answer it the same way.
/// </summary>
/// <remarks>
/// <para>
/// <c>MissingRefresh</c> decides which shows are searched for; the transfers
/// tick decides which grabs are cancelled for belonging to a show the owner
/// does not have. They are one policy, and while it was written out twice they
/// could drift apart: the plugin would grab a show and cancel it on the next
/// tick, or keep one it should never have started.
/// </para>
/// <para>
/// This is also the rule that put the owner on 479 grabs in an afternoon on
/// 24 August 2026, with Family Guy alone claiming 456 missing episodes. So it
/// is asserted from both sides at once, against one library, in one test.
/// </para>
/// </remarks>
public class OneRuleForWhoseShowItIsTests : IDisposable
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    /// <summary>The owner's show: it has an episode on disk.</summary>
    private const int Silo = 41;

    /// <summary>A row the server keeps that nobody asked for: nothing on disk.</summary>
    private const int FamilyGuy = 99;

    private const string SiloHash = "0123456789ABCDEF0123456789ABCDEF01234567";
    private const string FamilyGuyHash = "89ABCDEF0123456789ABCDEF0123456789ABCDEF";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-ownership-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AShowWithNothingOnDiskIsNeitherSearchedForNorLeftDownloading()
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")

            // The owner's: one episode on disk, one gap that has aired.
            .Show(Silo, "Silo", TelevisionLibrary, year: 2023)
            .Episode(Silo, 1, 1, hasFile: true, airDate: new DateTime(2023, 5, 5))
            .Episode(Silo, 3, 6, airDate: new DateTime(2026, 1, 1))

            // Not the owner's: a full episode list, a folder, the same library
            // id, and not one file.
            .Show(FamilyGuy, "Family Guy", TelevisionLibrary, year: 1999)
            .Episode(FamilyGuy, 1, 1, airDate: new DateTime(2020, 1, 1))
            .Episode(FamilyGuy, 1, 2, airDate: new DateTime(2020, 1, 8));

        HostLibrary library = new(query);

        // The search side: only the owner's show is tracked, and its gap is.
        IReadOnlyList<TrackedEpisode> tracked = await new MissingRefresh(
                library,
                new FakeTimeProvider(new DateTimeOffset(2026, 8, 25, 9, 0, 0, TimeSpan.Zero)))
            .DeriveAsync(new Profile(), CancellationToken.None);

        Assert.NotEmpty(tracked);
        Assert.All(tracked, episode => Assert.Equal("Silo", episode.ShowTitle));

        // The transfers side: one grab for each show, both downloading.
        GrabRepository grabs = await Grabs();

        await Grabbed(grabs, new EpisodeKey(Silo, 3, 6), "Silo", SiloHash);
        await Grabbed(grabs, new EpisodeKey(FamilyGuy, 1, 2), "Family Guy", FamilyGuyHash);

        StandingEngine engine = new StandingEngine()
            .Holding(Downloading(SiloHash))
            .Holding(Downloading(FamilyGuyHash));

        FakeProvider server = new();

        await new Transfers(
                engine,
                grabs,
                library,
                new Stager(server.Journal, server.Log),
                new EncodeDispatch(server, server.Journal, server.Log),
                server.Journal,
                server.Log,
                TimeProvider.System)
            .TickAsync(
                Path.Combine(_root, "incomplete"),
                Path.Combine(_root, "intake"),
                CancellationToken.None);

        (string InfoHash, bool DeleteFiles) removed = Assert.Single(engine.Removed);

        Assert.Equal(FamilyGuyHash, removed.InfoHash);
        Assert.True(removed.DeleteFiles, "Its download was left on the disk.");

        // And the owner's own grab is untouched by the same pass.
        StoredDownload open = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(SiloHash, open.InfoHash);
    }

    /// <summary>Still going, so nothing else in the tick acts on it.</summary>
    private static TorrentStatus Downloading(string hash)
    {
        return new(
            hash,
            hash,
            TorrentState.Downloading,
            BytesDone: 10_000,
            BytesTotal: 900_000_000,
            DownloadRateBytesPerSecond: 1_000,
            UploadRateBytesPerSecond: 0,
            Peers: 3,
            Seeds: 2,
            Ratio: 0,
            Eta: null,
            Error: null);
    }

    private static async Task Grabbed(GrabRepository grabs, EpisodeKey key, string show, string hash)
    {
        await grabs.RecordAsync(
            key,
            show,
            $"{show}.S{key.Season:00}E{key.Number:00}.1080p.WEB.H264-CAKES",
            "1337x",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            [key],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private async Task<GrabRepository> Grabs()
    {
        Store database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        TemporaryFolder.Forget(_root);
    }
}
