using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The encode is asked for through a port, so the day the contract lands is an
/// addition rather than surgery.
/// </summary>
/// <remarks>
/// <para>
/// Everything else this plugin asks of the server sits behind an interface in
/// <c>Core/Ports</c>. The encode did not: the cadence took the concrete
/// <c>EncodeDispatch</c>, which is the one class in the plugin that reaches
/// into the server by name — and the one part already scheduled to change, when
/// media-server #30 gives plugins <c>IPluginEncoder</c> and #35 gives them the
/// episode's id.
/// </para>
/// <para>
/// So this is that day, rehearsed: a second implementation, handed to the same
/// cadence, asked the same things. If it takes a class of its own and one line
/// of composition, the seam is where it should be. If it takes an edit to
/// <c>Transfers</c> — the thing keeping the owner's library filling — it is not.
/// </para>
/// </remarks>
public class TheEncodeIsAskedThroughAPortTests : IDisposable
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private const int Silo = 41;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-encode-port-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AnotherEncoderIsHandedTheStagedFileTheEpisodeAndTheShow()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        RecordingEncoder encoder = new();

        await Tick(grabs, encoder);

        (string StagedFile, Episode Episode, Show Show) asked =
            Assert.Single(encoder.Asked);

        Assert.Equal(Staged, asked.StagedFile);
        Assert.Equal(new EpisodeKey(Silo, 3, 6), asked.Episode.Key);

        // With the server's own id on it, taken from the answer this tick
        // already had rather than fetched again per episode.
        Assert.NotEqual(0, asked.Episode.ServerId);

        // The show's own library, which is what puts an anime episode in the
        // anime library and a television one in the tv library.
        Assert.Equal(TelevisionLibrary, asked.Show.LibraryId);
        Assert.Equal(LibraryKind.Television, asked.Show.Kind);

        // And the grab is waiting on it, which is what says the ask was taken.
        StoredDownload waiting = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Dispatched, waiting.State);
    }

    /// <remarks>
    /// A refusal leaves the file staged and the next tick asks again, without
    /// copying it a second time: an encode refused because the server could not
    /// yet identify the file is refused for a reason that can change. The
    /// cadence acts on "not taken" alone, whichever implementation said it.
    /// </remarks>
    [Fact]
    public async Task AnEncoderThatRefusesLeavesTheEpisodeStagedForTheNextTick()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        RecordingEncoder encoder = new() { Takes = false };

        await Tick(grabs, encoder);

        StoredDownload left = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Staged, left.State);
        Assert.Equal(Staged, left.StagedPath);
        Assert.True(File.Exists(Staged), "The staged file went, so the next tick has nothing to offer.");
    }

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    private string Staged => Path.Combine(
        Intake,
        EpisodeName.For("Silo", 2023, new EpisodeKey(Silo, 3, 6), "1080p", ".mkv"));

    /// <summary>One finished torrent, staged and offered to the encoder.</summary>
    private async Task Tick(GrabRepository grabs, RecordingEncoder encoder)
    {
        string release = "Silo.S03E06.1080p.WEB.H264-CAKES";

        Directory.CreateDirectory(Incomplete);

        using (FileStream writing = File.Create(Path.Combine(Incomplete, release + ".mkv")))
        {
            writing.SetLength(2_000_000);
        }

        StandingEngine engine = new StandingEngine().Holding(
            new TorrentStatus(
                Hash,
                release,
                TorrentState.Seeding,
                BytesDone: 2_000_000,
                BytesTotal: 2_000_000,
                DownloadRateBytesPerSecond: 0,
                UploadRateBytesPerSecond: 0,
                Peers: 3,
                Seeds: 2,
                Ratio: 0.4,
                Eta: null,
                Error: null),
            new TorrentFile(release + ".mkv", 2_000_000));

        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(Silo, "Silo", TelevisionLibrary, year: 2023)
            .Episode(Silo, 1, 1, hasFile: true)
            .Episode(Silo, 3, 6)
            .File(Silo, 3, 1, @"E:\tv\Silo (2023)\S03E01.mkv");

        FakeProvider server = new();

        await new Transfers(
                engine,
                grabs,
                new HostLibrary(query),
                new Stager(server.Journal, server.Log),
                encoder,
                server.Journal,
                server.Log,
                TimeProvider.System)
            .TickAsync(Incomplete, Intake, CancellationToken.None);
    }

    private static async Task Grabbed(GrabRepository grabs)
    {
        EpisodeKey episode = new(Silo, 3, 6);

        await grabs.RecordAsync(
            episode,
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [episode],
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
