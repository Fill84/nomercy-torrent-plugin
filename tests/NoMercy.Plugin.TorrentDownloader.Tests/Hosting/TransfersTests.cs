using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The cadence that makes the grabs into episodes.
/// </summary>
/// <remarks>
/// Sprint 6 built the grab, the staging and the encode dispatch, and nothing
/// ever called any of them: a download that finished sat in the incomplete
/// folder for ever and its episode showed as unavailable. This is the tick that
/// joins them, and every rule here is one that costs an episode when it is
/// missing.
/// </remarks>
public class TransfersTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-transfers-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <strong>F4.</strong> 0.3.4 only ever noticed a completion while it was
    /// running, so a download that finished during a restart sat there for ever
    /// and the episode was never dispatched. A finished torrent is staged on the
    /// first tick, whenever it finished.
    /// </remarks>
    [Fact]
    public async Task ATorrentThatFinishedWhileTheServerWasDownIsStagedOnTheFirstTick()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        // The server knows the staged file once it is there, which is what the
        // dispatch asks it for: the id is the server's own, never the filename.
        server.Files.Matches = [(Path.Combine(Intake, Path.GetFileName(episode)), "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(Intake, Path.GetFileName(episode))), "It was never staged.");
        Assert.False(File.Exists(episode), "The download was left where it was.");
        Assert.NotNull(server.Dispatcher.Job);
    }

    /// <remarks>
    /// Both halves of a failure, or the episode is lost one way or the other:
    /// blacklisting without returning it leaves it looking grabbed for ever,
    /// and returning it without blacklisting has the next cycle choose the same
    /// release and fail the same way for as long as the plugin runs.
    /// </remarks>
    [Fact]
    public async Task ATorrentTheClientHasFailedIsBlacklistedAndItsEpisodesGoBackToMissing()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Error, Error = "no peer sent its metadata" });

        await Transfers(engine, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Contains(Hash, await grabs.BlacklistedAsync(CancellationToken.None));

        // Finished with, either way, so recovery does not re-add it on the
        // next tick.
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// A torrent the client has never heard of is re-added from the magnet the
    /// store kept, not searched for again: its bytes are still on disk with its
    /// resume file, so this costs a verification pass rather than a download.
    /// </remarks>
    [Fact]
    public async Task AGrabTheClientHasLostIsReAddedFromTheMagnetTheStoreKept()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        StandingEngine engine = new();

        await Transfers(engine, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        TorrentRequest again = Assert.Single(engine.Taken);

        Assert.StartsWith("magnet:?xt=urn:btih:", again.Source, StringComparison.Ordinal);
        Assert.Equal(Incomplete, again.DownloadFolder);
    }

    /// <remarks>
    /// Something the plugin has no record of is stopped and its files kept. It
    /// may be half a film the owner has been waiting for, and a record can be
    /// lost by a restore of an older database.
    /// </remarks>
    [Fact]
    public async Task ATorrentThePluginHasNoRecordOfIsStoppedAndItsFilesKept()
    {
        GrabRepository grabs = await Grabs();

        StandingEngine engine = new StandingEngine().Holding(Finished());

        await Transfers(engine, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        (string InfoHash, bool DeleteFiles) stopped = Assert.Single(engine.Removed);

        Assert.Equal(Hash, stopped.InfoHash);
        Assert.False(stopped.DeleteFiles);
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static EpisodeKey Episode => new(41, 3, 6);

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    private static TorrentStatus Finished()
    {
        return new(
            Hash,
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            TorrentState.Seeding,
            BytesDone: 900_000_000,
            BytesTotal: 900_000_000,
            DownloadRateBytesPerSecond: 0,
            UploadRateBytesPerSecond: 0,
            Peers: 3,
            Seeds: 2,
            Ratio: 0.4,
            Eta: null,
            Error: null);
    }

    private static async Task Grabbed(GrabRepository grabs)
    {
        await grabs.RecordAsync(
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

    /// <summary>A file really on disk, where the download would have left it.</summary>
    private string Downloaded(string name, long length)
    {
        Directory.CreateDirectory(Incomplete);

        string path = Path.Combine(Incomplete, name);

        using (FileStream writing = File.Create(path))
        {
            writing.SetLength(length);
        }

        return path;
    }

    private static FakeProvider Server()
    {
        return new();
    }

    private static Transfers Transfers(StandingEngine engine, GrabRepository grabs, FakeProvider server)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library("library-tv", "Television", "tv")
            .Show(41, "Silo", "library-tv");

        return new(
            engine,
            grabs,
            new HostLibrary(query),
            new Stager(server.Journal, server.Log),
            new EncodeDispatch(server, server.Journal, server.Log),
            server.Journal,
            server.Log);
    }

    private async Task<GrabRepository> Grabs()
    {
        Database database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
