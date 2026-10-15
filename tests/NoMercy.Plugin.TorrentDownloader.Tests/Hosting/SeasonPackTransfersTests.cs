using System.Text;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>A season pack the search took, while it downloads.</summary>
/// <remarks>
/// <para>
/// <c>docs/specs/release-names.md</c> § Season packs, GitHub issue #1: a pack's grab answers for every episode of
/// its season the run searched for, and only their videos are downloaded. An episode the pack turns out not to
/// hold stays missing and is searched for singly.
/// </para>
/// <para>
/// The files are the owner's own Dark Matter season pack, file for file: episodes one to nine and two text files.
/// The grab answers for episodes three and ten, as one that a run took while episode ten was missing too would.
/// </para>
/// </remarks>
public sealed class SeasonPackTransfersTests : IDisposable
{
    private const string TvLibrary = "01KZGKX2G0966V80H26EKGG5T1";
    private const int DarkMatter = 7;
    private const string Hash = "447F09D4ABFF9EF252E41E4322AC7FDC05D38443";
    private const string Pack = "Dark.Matter.2024.S01.1080p.ATVP.WEB-DL.DDP5.1.Atmos.H.264-FLUX";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-pack-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Once the files are known the pack is seen to hold episode three and not episode ten, so the grab stops
    /// answering for ten — and the next run, which leaves out every episode an open grab answers for, searches
    /// for it.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeThePackDoesNotHoldIsSearchedForSinglyAfterwards()
    {
        GrabRepository grabs = await GrabsAsync();
        StandingEngine engine = new StandingEngine().Holding(Downloading(), [.. Files()]);

        await Transfers(engine, grabs).TickAsync(Incomplete, Intake, CancellationToken.None);

        TrackedEpisode three = Missing(3);
        TrackedEpisode ten = Missing(10);

        IReadOnlyList<TrackedEpisode> searched = OpenGrabs.Excluding(
            [three, ten],
            [.. (await grabs.OpenAsync(CancellationToken.None)).SelectMany(one => one.Covers)]);

        Assert.Equal([ten], searched);
    }

    /// <remarks>
    /// And a pack that is already whole when it is first looked at — it finished while the server was down, or
    /// before its metadata was read on a pass — lets go of episode ten on the pass that stages it, so ten is
    /// neither staged nor waited for, and the next run searches for it.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeAFinishedPackDoesNotHoldIsLetGoOfAsItIsStaged()
    {
        GrabRepository grabs = await GrabsAsync();
        StandingEngine engine = new StandingEngine().Holding(
            Downloading() with { State = TorrentState.Finished, BytesDone = 8L * 1024 * 1024 * 1024 },
            [.. Files()]);

        await Transfers(engine, grabs).TickAsync(Incomplete, Intake, CancellationToken.None);

        StoredDownload pack = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal([new EpisodeKey(DarkMatter, 1, 3)], pack.Covers);
    }

    /// <remarks>
    /// A pack the client lost — a restart — is added back for the same episodes, so it goes on downloading only
    /// their videos.
    /// </remarks>
    [Fact]
    public async Task APackAddedBackIsAddedForTheEpisodesItAnswersFor()
    {
        GrabRepository grabs = await GrabsAsync();
        StandingEngine engine = new();

        await Transfers(engine, grabs).TickAsync(Incomplete, Intake, CancellationToken.None);

        TorrentRequest again = Assert.Single(engine.Taken);

        Assert.Equal([new EpisodeKey(DarkMatter, 1, 3), new EpisodeKey(DarkMatter, 1, 10)], again.Episodes);
    }

    private async Task<GrabRepository> GrabsAsync()
    {
        Store database = new(_root);

        await database.MigrateAsync(CancellationToken.None);

        GrabRepository grabs = new(database);

        await grabs.RecordAsync(
            new(DarkMatter, 1, 3),
            "Dark Matter",
            Pack,
            "The Pirate Bay",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}&dn={Uri.EscapeDataString(Pack)}",
            [new(DarkMatter, 1, 3), new(DarkMatter, 1, 10)],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        return grabs;
    }

    private static TrackedEpisode Missing(int number)
    {
        return new(new(DarkMatter, 1, number), "Dark Matter", 2024, LibraryKind.Television, null, new DateOnly(2024, 5, 8), EpisodeState.Missing);
    }

    private static TorrentStatus Downloading()
    {
        return new(
            Hash,
            Pack,
            TorrentState.Downloading,
            BytesDone: 0,
            BytesTotal: 8L * 1024 * 1024 * 1024,
            DownloadRateBytesPerSecond: 0,
            UploadRateBytesPerSecond: 0,
            Peers: 3,
            Seeds: 1,
            Ratio: null,
            Eta: null,
            Error: null);
    }

    /// <summary>The pack's files as the client reports them, at the four gigabytes each episode really is.</summary>
    private static IReadOnlyList<TorrentFile> Files()
    {
        return
        [
            .. Encoding.UTF8.GetString(RealSwarm.Fixture("dark-matter-s01-pack.txt"))
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(path => new TorrentFile(path, path.EndsWith(".mkv", StringComparison.Ordinal) ? 4L * 1024 * 1024 * 1024 : 500)),
        ];
    }

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    private static Transfers Transfers(ITorrentEngine engine, GrabRepository grabs)
    {
        FakeProvider server = new();
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TvLibrary, "TV", "tv")
            .Show(DarkMatter, "Dark Matter", TvLibrary, year: 2024);

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
            null);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
