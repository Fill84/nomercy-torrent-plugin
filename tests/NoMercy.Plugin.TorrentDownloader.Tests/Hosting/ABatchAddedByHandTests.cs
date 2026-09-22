using NoMercy.Plugin.TorrentDownloader.Bittorrent;
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
/// A season pack the owner pasted in by hand, whose files abbreviate the show's name.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Nothing happened when it finished.</strong> On 18 September 2026 a batch of
/// <em>Classroom of the Elite</em> was added by hand on another owner's server, downloaded all 3.7 GB of it,
/// showed as finished and was never moved out of the download folder. A torrent added by hand covers no
/// episode, so what it holds is read out of its files — and a fansub group names its files after an
/// abbreviation the library has never heard of: <c>[Judas] Youjitsu - S04E01v2.mkv</c> for a show the owner
/// has as <em>Classroom of the Elite</em>. No file named a show in a library, so nothing was staged,
/// nothing was encoded, and the download sat there complete.
/// </para>
/// <para>
/// The torrent's own name carries both titles — the romanised one and, in brackets, the one the library
/// uses — and a multi-file torrent puts every file under it. Everything here is read from the real torrent,
/// which is the one the owner's magnet pointed at.
/// </para>
/// </remarks>
public sealed class ABatchAddedByHandTests : IDisposable
{
    private const string AnimeLibrary = "01KZGKX2G0966V80H26EKGG5T2";

    /// <summary>The show as the owner's library has it, which is not what the files call it.</summary>
    private const int ClassroomOfTheElite = 77;

    private const string Hash = "8B8B3B629BBDDFF97906261BFEBC5B2CB15A94F1";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-batch-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The episodes come from the files, as they always did: the files are the only thing that says which
    /// episode is which. It is the show that cannot be read there, and the folder the torrent puts its files
    /// in says it.
    /// </remarks>
    [Fact]
    public void TheEpisodesOfABatchAreReadWhenItsFilesAbbreviateTheShowsName()
    {
        IReadOnlyList<EpisodeKey> found = Staging.Discover(Files(), [Show()]);

        Assert.Equal(
            [.. Enumerable.Range(1, 16).Select(number => new EpisodeKey(ClassroomOfTheElite, 4, number))],
            [.. found.OrderBy(one => one.Number)]);
    }

    /// <remarks>
    /// And nothing is placed somewhere plausible. A torrent naming a show the owner does not have yields
    /// nothing at all, which is what leaves it for the owner to add rather than filing it under whichever
    /// show happened to be first.
    /// </remarks>
    [Fact]
    public void ABatchOfAShowTheOwnerDoesNotHaveIsStillDiscoveredAsNothing()
    {
        Assert.Empty(Staging.Discover(Files(), [new(41, "Silo", 2023, AnimeLibrary, "Anime", LibraryKind.Anime, "Silo")]));
    }

    /// <remarks>
    /// A name may end in one show's title and be another's: <em>Elite</em> is a programme of its own, and
    /// <em>Classroom of the Elite</em> ends in it. The longest title the folder carries is the show, whichever
    /// order the server answers its libraries in.
    /// </remarks>
    [Fact]
    public void ABatchGoesToTheShowWhoseTitleTheFolderCarriesInFull()
    {
        Show elite = new(42, "Elite", 2018, AnimeLibrary, "Anime", LibraryKind.Anime, "Elite");

        Assert.All(
            Staging.Discover(Files(), [elite, Show()]),
            one => Assert.Equal(ClassroomOfTheElite, one.ShowId));

        Assert.All(
            Staging.Discover(Files(), [Show(), elite]),
            one => Assert.Equal(ClassroomOfTheElite, one.ShowId));
    }

    /// <remarks>
    /// The outcome the owner is after: every episode out of the download folder, named after the show the
    /// library has, and an encode asked for each.
    /// </remarks>
    [Fact]
    public async Task EveryEpisodeOfABatchAddedByHandIsStagedAndDispatched()
    {
        GrabRepository grabs = await Grabs();
        TorrentMetadata torrent = Torrent();

        await grabs.RecordAsync(
            new(0, 0, 0),
            string.Empty,
            torrent.Name,
            "by hand",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}&dn={Uri.EscapeDataString(torrent.Name)}",
            [],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // The real torrent's names, at a length a test can really write: staging refuses a file that is not
        // the length the torrent says, which is how a download stopped half-way is told from a finished one,
        // and sixteen real episodes are 3.7 GB. The names are what is under test.
        IReadOnlyList<TorrentFile> files = [.. Files().Select(one => one with { Length = 64 * 1024 })];

        foreach (TorrentFile file in files)
        {
            Downloaded(file.Path);
        }

        StandingEngine engine = new StandingEngine().Holding(Finished(torrent), [.. files]);
        FakeProvider server = Server();

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        for (int number = 1; number <= 16; number++)
        {
            string staged = Path.Combine(
                Intake,
                EpisodeName.For("Classroom of the Elite", 2017, new(ClassroomOfTheElite, 4, number), "1080p", ".mkv"));

            Assert.True(
                File.Exists(staged),
                $"Episode {number} was never staged. In the intake folder: {string.Join(", ", Directory.Exists(Intake) ? Directory.GetFiles(Intake).Select(Path.GetFileName) : [])}");
        }

        Assert.Equal(16, server.Encoder.Dispatches);
    }

    /// <summary>The real torrent the owner's magnet pointed at.</summary>
    private static TorrentMetadata Torrent()
    {
        return TorrentMetadata.Read(RealSwarm.Fixture("classroom-of-the-elite-s04-batch.torrent"));
    }

    /// <summary>Its files, exactly as the client reports them — under the folder it downloads into.</summary>
    private static IReadOnlyList<TorrentFile> Files()
    {
        TorrentMetadata torrent = Torrent();

        return [.. torrent.Files.Select(file => new TorrentFile(torrent.PathUnderFolder(file), file.Length))];
    }

    private static Show Show()
    {
        return new(ClassroomOfTheElite, "Classroom of the Elite", 2017, AnimeLibrary, "Anime", LibraryKind.Anime, "Classroom of the Elite");
    }

    private static TorrentStatus Finished(TorrentMetadata torrent)
    {
        return new(
            Hash,
            torrent.Name,
            TorrentState.Finished,
            BytesDone: torrent.TotalLength,
            BytesTotal: torrent.TotalLength,
            DownloadRateBytesPerSecond: 0,
            UploadRateBytesPerSecond: 0,
            Peers: 0,
            Seeds: 0,
            Ratio: 1.0,
            Eta: null,
            Error: null);
    }

    /// <summary>
    /// A file really on disk, where the download left it.
    /// </summary>
    /// <remarks>
    /// Short, where the real one is a quarter of a gigabyte. Nothing in staging reads a length off the disk —
    /// what is under test is which file answers for which episode, and that is its name.
    /// </remarks>
    private void Downloaded(string path)
    {
        string full = Path.Combine(Incomplete, path);

        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using FileStream writing = File.Create(full);

        writing.SetLength(64 * 1024);
    }

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    private static FakeProvider Server()
    {
        return new();
    }

    private Transfers Transfers(ITorrentEngine engine, GrabRepository grabs, FakeProvider server)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(AnimeLibrary, "Anime", "anime")
            .Show(ClassroomOfTheElite, "Classroom of the Elite", AnimeLibrary, year: 2017);

        for (int number = 1; number <= 16; number++)
        {
            query.Episode(ClassroomOfTheElite, 4, number);
        }

        return new(
            engine,
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            EncodeGateway.For(server.Encoder, server.Journal, server.Log),
            server.Journal,
            server.Log,
            TimeProvider.System,
            null,
            null);
    }

    private async Task<GrabRepository> Grabs()
    {
        Store database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
