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
/// One tick asks the library each question once.
/// </summary>
/// <remarks>
/// <para>
/// The transfers cadence runs every minute. It asked the library for its shows
/// once per staged file and once per dispatch, for a show's files once per
/// dispatch, and for a show's episodes from two places that each kept a cache
/// of their own. A tick staging four episodes made eight round trips for a list
/// that cannot change while the tick is running.
/// </para>
/// <para>
/// None of it was wrong, and none of it shows in an outcome — which is why it
/// is counted here. These are the only tests in the suite that assert a number
/// of calls rather than a result, and they assert it because the cost is the
/// whole of the fault.
/// </para>
/// </remarks>
public class OneQuestionPerTickTests : IDisposable
{
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

    private const int Silo = 41;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-per-tick-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task OneTickStagingFourEpisodesAsksForTheShowsOnce()
    {
        GrabRepository grabs = await Grabs();
        FakeLibraryQuery query = Library();
        FakeProvider server = new();
        StandingEngine engine = new();

        List<(string Path, string Id)> listed = [];

        for (int number = 1; number <= 4; number++)
        {
            EpisodeKey key = new(Silo, 3, number);
            string hash = Hash(number);
            string release = $"Silo.S03E0{number}.1080p.WEB.H264-CAKES";

            Downloaded(release + ".mkv");

            await Grabbed(grabs, key, release, hash);

            engine.Holding(Finished(hash, release), new TorrentFile(release + ".mkv", Length));

            // The server's own listing of the intake folder, which is what says
            // a staged file is one it will encode.
            listed.Add((Staged(key), (4400 + number).ToString()));
        }

        server.Files.Matches = listed;

        await Transfers(engine, grabs, query, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        // The tick really did the work, or the counts below are the counts of a
        // tick that did nothing.
        Assert.Equal(4, server.Dispatcher.Dispatches);

        Assert.Equal(1, query.Shows);
        Assert.Equal(1, query.Libraries);

        // Where the show's episodes already are decides which of a library's
        // folders the encode is sent to, and one tick has one answer for that
        // too.
        Assert.Equal(1, query.Files);
    }

    /// <remarks>
    /// Whose show it is and whether an encode has landed are read from the same
    /// call, from two places, on the same tick — and each kept a cache of its
    /// own, so one question was fetched twice.
    /// </remarks>
    [Fact]
    public async Task OneTickAsksForAShowsEpisodesOnceHoweverManyGrabsMentionIt()
    {
        GrabRepository grabs = await Grabs();
        FakeLibraryQuery query = Library();
        FakeProvider server = new();

        // Still downloading, so whose show it is is asked about this one.
        EpisodeKey downloading = new(Silo, 3, 1);

        await Grabbed(grabs, downloading, "Silo.S03E01.1080p.WEB.H264-CAKES", Hash(1));

        // Dispatched, so whether the encode has landed is asked about that one:
        // the same question, of the same show, on the same tick.
        EpisodeKey dispatched = new(Silo, 3, 2);

        await Grabbed(grabs, dispatched, "Silo.S03E02.1080p.WEB.H264-CAKES", Hash(2));
        await grabs.StagedAsync(Hash(2), Staged(dispatched), CancellationToken.None);
        await grabs.StateAsync(Hash(2), GrabState.Dispatched, CancellationToken.None);

        StandingEngine engine = new StandingEngine().Holding(Downloading(Hash(1)));

        await Transfers(engine, grabs, query, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        // Neither was cancelled and neither was finished, so both questions
        // were really asked.
        Assert.Empty(engine.Removed);
        Assert.Equal(2, (await grabs.OpenAsync(CancellationToken.None)).Count);

        Assert.Equal(1, query.Episodes);
    }

    /// <summary>The owner's show: one episode on disk, and gaps either side.</summary>
    private static FakeLibraryQuery Library()
    {
        return new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(Silo, "Silo", TelevisionLibrary, year: 2023)
            .Episode(Silo, 1, 1, hasFile: true)
            .Episode(Silo, 3, 1)
            .Episode(Silo, 3, 2)
            .Episode(Silo, 3, 3)
            .Episode(Silo, 3, 4);
    }

    /// <summary>Forty characters, all the same, so one hash reads as one grab.</summary>
    private static string Hash(int number)
    {
        return new string((char)('A' + number), 40);
    }

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    /// <summary>
    /// Where an episode ends up, built from the plugin's own naming rule rather
    /// than written out, so a change to that rule shows up here as a failure.
    /// </summary>
    private string Staged(EpisodeKey episode)
    {
        return Path.Combine(Intake, EpisodeName.For("Silo", 2023, episode, "1080p", ".mkv"));
    }

    /// <summary>
    /// How big a download is here. Small on purpose: staging really copies the
    /// bytes and checks the length afterwards, and four episodes at the size a
    /// real one would be is nearly four gigabytes of copying for a test that
    /// counts calls. The only size rule staging has is relative — a sample is
    /// small beside something bigger in the same torrent — and one video that
    /// is the whole torrent is the episode whatever it weighs.
    /// </summary>
    private const long Length = 2_000_000;

    private void Downloaded(string name)
    {
        Directory.CreateDirectory(Incomplete);

        using FileStream writing = File.Create(Path.Combine(Incomplete, name));

        writing.SetLength(Length);
    }

    private static TorrentStatus Finished(string hash, string name)
    {
        return new(
            hash,
            name,
            TorrentState.Seeding,
            BytesDone: Length,
            BytesTotal: Length,
            DownloadRateBytesPerSecond: 0,
            UploadRateBytesPerSecond: 0,
            Peers: 3,
            Seeds: 2,
            Ratio: 0.4,
            Eta: null,
            Error: null);
    }

    private static TorrentStatus Downloading(string hash)
    {
        return Finished(hash, hash) with { State = TorrentState.Downloading, BytesDone = 10_000 };
    }

    private static async Task Grabbed(GrabRepository grabs, EpisodeKey key, string release, string hash)
    {
        await grabs.RecordAsync(
            key,
            "Silo",
            release,
            "1337x",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            [key],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
    }

    private static Transfers Transfers(
        StandingEngine engine,
        GrabRepository grabs,
        FakeLibraryQuery query,
        FakeProvider server)
    {
        return new(
            engine,
            grabs,
            new HostLibrary(query),
            new Stager(server.Journal, server.Log),
            new EncodeDispatch(server, server.Journal, server.Log),
            server.Journal,
            server.Log,
            TimeProvider.System);
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
