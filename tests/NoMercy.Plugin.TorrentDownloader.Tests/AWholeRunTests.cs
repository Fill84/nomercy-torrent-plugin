using System.Net;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The plugin driven from its name sources to a grab to the torrent client, with nothing real but the sockets.
/// </summary>
/// <remarks>
/// <para>
/// <c>S17-05</c>. One name source (PreDB.net) and one indexer (The Pirate Bay) answer the captures of 15 October
/// 2026 for South Park S29E03; the one tracker answers with a client of this plugin's own on this machine, or
/// with nobody. Everything between — the feed, the search, the decision, the grab, the client, the transfers
/// pass and the store — is the plugin's own, as it ships.
/// </para>
/// <para>
/// The capture's hash is the one thing edited: a real swarm can only be for a torrent made here, so the CAKES
/// row carries that torrent's hash instead of the one The Pirate Bay listed. Nothing else about the page is
/// touched.
/// </para>
/// <para>
/// Each test here covers one line of the plugin's wiring that no other test reaches, and each was seen to fail
/// with that line taken out: the transfers pass started once a grab's row exists, the host making the download's
/// file sparse, the open hashes a full pass does not take again, and the old-delivery check running after the
/// search and never on a hand-run.
/// </para>
/// </remarks>
public sealed class AWholeRunTests : IDisposable
{
    private const string Library = "01HQ5W4AVF30N10RT6XCF6AJHM";
    private const int SouthPark = 41;
    private const string Cakes = "South.Park.S29E03.1080p.WEB.H264-CAKES";
    private const string CakesHashOnThePirateBay = "70AFDB530DD7CD04D1C36E142CC523A56E738DE8";
    private const string Tracker = "http://tracker.test/announce";

    private static readonly EpisodeKey S29E03 = new(SouthPark, 29, 3);
    private static readonly EpisodeKey S29E04 = new(SouthPark, 29, 4);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-whole-run-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The client takes a torrent before its grab's row exists, and a transfers pass in that gap stops it as
    /// nobody's. The pass started once the row is written (<c>CycleWriter</c>'s <c>handedOver</c>) is what lets
    /// the row and the client agree within the run: it is the pass that first reads the grab as downloading. With
    /// nobody seeding, no download finishes and no other pass runs, so only that one can move the row.
    /// </remarks>
    [Fact]
    public async Task ARunThatGrabsHandsTheDownloadOverWithinTheRun()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        using Run run = await StartAsync(Missing(S29E03), seeding: false, stopping.Token);

        await run.Plugin.RunCycleAsync(stopping.Token);

        EpisodeOutcome outcome = run.Outcome();

        Assert.True(outcome.HandedOver, outcome.Detail);
        Assert.Equal(run.Hash, outcome.InfoHash);

        StoredDownload grab = await UntilAsync(
            async () => (await run.Grabs.OpenAsync(CancellationToken.None)).SingleOrDefault(one => one.State == GrabState.Downloading),
            stopping.Token,
            "the grab was never read as downloading: no transfers pass ran after its row was written");

        Assert.Equal(run.Hash, grab.InfoHash);
        Assert.Equal([S29E03], grab.Covers);
    }

    /// <remarks>
    /// The download's file is made sparse by the host (<c>IPluginStorage.MarkSparseAsync</c>, media-server #609),
    /// reached from the plugin's engine through <c>HostSparse.Through</c>. Only NTFS has the flag; everywhere
    /// else the host answers false and the file is sparse by default, so the flag is asserted on Windows alone
    /// and the download landing is asserted everywhere.
    /// </remarks>
    [Fact]
    public async Task TheDownloadLandsSparseInTheIncompleteFolder()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        using Run run = await StartAsync(Missing(S29E03), seeding: true, stopping.Token);

        await run.Plugin.RunCycleAsync(stopping.Token);

        Assert.True(run.Outcome().HandedOver, run.Outcome().Detail);

        string landed = await UntilAsync(
            () => Task.FromResult(Directory.EnumerateFiles(run.Incomplete, "*.mkv", SearchOption.AllDirectories).FirstOrDefault()),
            stopping.Token,
            "no video of the download ever appeared in the incomplete folder");

        Assert.Equal(run.Torrent.Name, Path.GetFileName(landed));

        if (OperatingSystem.IsWindows())
        {
            Assert.True(
                File.GetAttributes(landed).HasFlag(FileAttributes.SparseFile),
                $"{landed} was written without the host making it sparse.");
        }
    }

    /// <remarks>
    /// A torrent already in the client is not taken again (<c>CycleOptions.OpenHashes</c>). The owner added the
    /// CAKES torrent by hand, so its grab is open and answers for no episode; the run then finds the same hash as
    /// the one named release for S29E03 and passes it over, leaving the hand-added row as it was. Taken again, the
    /// row would have been rewritten as the episode's grab and the client asked for a torrent it already held.
    /// </remarks>
    [Fact]
    public async Task ARunDoesNotTakeAHashTheClientAlreadyHolds()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        using Run run = await StartAsync(Missing(S29E03), seeding: false, stopping.Token);

        (string? byHand, string? refusal) = await run.Plugin.AddTorrentAsync($"magnet:?xt=urn:btih:{run.Hash}", stopping.Token);

        Assert.Null(refusal);
        Assert.Equal(run.Hash, byHand);

        await run.Plugin.RunCycleAsync(stopping.Token);

        EpisodeOutcome outcome = run.Outcome();

        Assert.False(outcome.HandedOver, "the run took a torrent the client was already downloading");

        StoredDownload kept = Assert.Single(await run.Grabs.EveryAsync(CancellationToken.None));

        Assert.Equal(run.Hash, kept.InfoHash);
        Assert.True(kept.ByHand, "the hand-added grab was rewritten as the episode's");
        Assert.Empty(kept.Covers);
        Assert.Contains(
            run.Plugin.Journal.Snapshot().History,
            entry => entry.Detail?.Contains("already being downloaded", StringComparison.Ordinal) == true);
    }

    /// <remarks>
    /// A delivery from before the plugin wrote down where a release came from (<c>named</c> null) is checked
    /// against the name sources once, after the run's own search and never before it: before, it held up every
    /// run after the update by as many paced questions as there were deliveries. Here the run's search is for
    /// S29E04 and the check is for S29E03's delivery, so the order of the two questions to PreDB.net is the order
    /// of the two steps.
    /// </remarks>
    [Fact]
    public async Task ADeliveryFromBeforeNamedIsCheckedAfterTheSearch()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        using Run run = await StartAsync(DeliveredAndMissing(), seeding: false, stopping.Token);

        await run.Delivered(Cakes, S29E03, stopping.Token);
        await run.Plugin.RunCycleAsync(stopping.Token);

        int search = run.Http.Attempts.FindIndex(one => AsksPreDb(one, "S29E04"));
        int check = run.Http.Attempts.FindIndex(one => AsksPreDb(one, "S29E03"));

        Assert.True(search >= 0, "the run never searched for the missing episode");
        Assert.True(check >= 0, "the run never checked the old delivery against the name sources");
        Assert.True(check > search, "the old delivery was checked before the run's own search");

        StoredDownload delivery = Assert.Single(await run.Grabs.EveryAsync(CancellationToken.None));

        Assert.True(delivery.Named, "the delivery was not written down as a release the name sources name");
    }

    /// <remarks>
    /// The owner asked for one episode, not for the past: a hand-run searches for that episode and checks no old
    /// delivery.
    /// </remarks>
    [Fact]
    public async Task AHandRunForOneEpisodeChecksNoOldDelivery()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        using Run run = await StartAsync(DeliveredAndMissing(), seeding: false, stopping.Token);

        await run.Delivered(Cakes, S29E03, stopping.Token);

        Assert.True(await run.Plugin.StartSearchAsync(S29E04), "the hand-run was refused");

        // The search is over once the plugin sums it up in its log, which it does after the old-delivery check
        // when there is one: "not running" comes too early, since a hand-run is open only while it searches.
        await UntilAsync(
            () => Task.FromResult(run.Said.Any(line => line.StartsWith("The search looked at", StringComparison.Ordinal)) ? true : (bool?)null),
            stopping.Token,
            "the hand-run never finished its search");

        Assert.Contains(run.Http.Attempts, one => AsksPreDb(one, "S29E04"));

        Assert.DoesNotContain(run.Http.Attempts, one => AsksPreDb(one, "S29E03"));
        Assert.Null(Assert.Single(await run.Grabs.EveryAsync(CancellationToken.None)).Named);
    }

    private static FakeLibraryQuery Missing(EpisodeKey episode)
    {
        return new FakeLibraryQuery()
            .Library(Library, "Series", "tv")
            .Show(SouthPark, "South Park", Library, 1997, folder: "/South.Park.(1997)")
            .Episode(SouthPark, episode.Season, episode.Number, "An episode", new DateTime(2026, 10, 15), hasFile: false);
    }

    /// <summary>S29E03 on disk, from a delivery; S29E04 missing.</summary>
    private static FakeLibraryQuery DeliveredAndMissing()
    {
        return Missing(S29E04)
            .Episode(SouthPark, 29, 3, "Delivered", new DateTime(2026, 10, 15), hasFile: true);
    }

    private static bool AsksPreDb(HttpRequestMessage request, string episode)
    {
        return request.RequestUri is Uri asked
               && asked.Host == "api.predb.net"
               && asked.Query.Contains(episode, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<Run> StartAsync(FakeLibraryQuery shelves, bool seeding, CancellationToken ct)
    {
        string data = Path.Combine(_root, "data");
        string incomplete = Path.Combine(_root, "incomplete");
        string intake = Path.Combine(_root, "intake");

        foreach (string folder in new[] { data, incomplete, intake })
        {
            Directory.CreateDirectory(folder);
        }

        // The torrent the whole run is about: a video named as the CAKES release, private so that this client
        // uploads on it, whole in a seeding client of this plugin's own when the test wants a swarm.
        RealSwarm swarm = new(Path.Combine(_root, "swarm"));
        byte[] content = new byte[64 * 1024];

        new Random(2903).NextBytes(content);

        byte[] bytes = RealSwarm.Torrent(content, pieceLength: 4096, name: Cakes + ".mkv");
        TorrentMetadata torrent = TorrentMetadata.Read(bytes);
        BittorrentEngine? seeder = null;

        if (seeding)
        {
            string file = Path.Combine(_root, "the.torrent");

            File.WriteAllBytes(file, bytes);
            RealSwarm.Seeded(torrent, content, swarm.Folder("seed"));

            seeder = swarm.Engine("seed", new SilentTrackers());
            seeder.Start();

            await seeder.AddAsync(new(file, [], swarm.Folder("seed")), ct);
        }

        FakeHttp http = Wire(torrent.InfoHash, seeder?.Port);
        FakeGrants grants = new();

        grants.Grant("api.predb.net");
        grants.Grant("apibay.org");

        TorrentDownloaderPlugin plugin = new() { Network = http };
        FakePluginContext context = new()
        {
            DataFolderPath = data,
            Shelves = shelves,
            Permits = grants,
            Encodes = new FakeEncoder(),
        };

        plugin.Initialize(context);

        // A cycle that finished a moment ago, written before the folders are saved. A plugin that has never
        // run is due the moment it is configured, and that cycle — started by the clock, joined by the test's
        // own call and so run twice over — is not the one these tests drive. Asked of the repository first so
        // the plugin's own has made the table.
        GrabRepository grabs = await plugin.GrabsAsync(ct);

        await new CadenceRepository(TestStore.In(data)).RecordFinishedAsync(JobNames.Cycle, DateTimeOffset.UtcNow, ct);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };

        // A port of the machine's choosing: the tests of this project run beside each other, and two clients
        // on the shipped port would be one client and one refusal.
        settings.Client.ListenPort = 0;
        settings.Client.DefaultTrackers.Add(Tracker);

        foreach (string source in new CatalogueLoader(new CapturingLogger()).Load().Select(one => one.Name))
        {
            if (source is not ("PreDB.net" or "The Pirate Bay"))
            {
                settings.DisabledDefaultSources.Add(source);
            }
        }

        await plugin.Settings.SaveAsync(settings, ct);

        Assert.Empty(await plugin.SaveShowSettingsAsync(
            SouthPark,
            new Dictionary<string, string?> { ["switchedOn"] = "true", ["quality"] = "1080p" },
            ct));

        return new(plugin, context, http, grabs, torrent, seeder, incomplete);
    }

    /// <summary>The name source, the indexer and the tracker, answering as captured, the tracker with the seeder or nobody.</summary>
    private static FakeHttp Wire(string hash, int? seederPort)
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();
        SourceDefinition pirateBay = shipped.Single(one => one.Name == "The Pirate Bay");

        string Capture(string name)
        {
            return Encoding.UTF8.GetString(RealSwarm.Fixture(name)).Replace(CakesHashOnThePirateBay, hash, StringComparison.Ordinal);
        }

        static bool Asks(HttpRequestMessage request, string address)
        {
            return request.RequestUri?.ToString() == new Uri(address).ToString();
        }

        return new FakeHttp()
            .Answers(one => one.RequestUri is { Host: "api.predb.net", Query: "" }, () => Capture("names-predbnet-feed.xml"))
            .Answers(one => AsksPreDb(one, "S29E03"), () => Capture("names-predbnet-search-south-park-s29e03.xml"))
            .Answers(one => AsksPreDb(one, "S29E04"), () => Capture("names-predbnet-search-south-park-s29e04.xml"))
            .Answers(one => Asks(one, Query.WriteExact(pirateBay.SearchAddress!, Cakes)), () => Capture("s29e03-apibay-cakes-exact.json"))
            .Answers(one => Asks(one, Query.Write(pirateBay.SearchAddress!, Cakes, pirateBay.Query)), () => Capture("s29e03-apibay-cakes-words.json"))
            .Answers(one => Asks(one, Query.WriteExact(pirateBay.SearchAddress!, "South Park S29E03")), () => Capture("s29e03-apibay-south-park-s29e03-exact.json"))
            .Answers(one => Asks(one, Query.Write(pirateBay.SearchAddress!, "South Park S29E03", pirateBay.Query)), () => Capture("s29e03-apibay-south-park-s29e03-words.json"))
            .Answers(one => one.RequestUri?.Host == "tracker.test", _ => RealSwarm.Announce(seederPort))
            .Otherwise(HttpStatusCode.NotFound);
    }

    /// <summary>Polls until <paramref name="read"/> answers something, or the bound says it is a hang.</summary>
    private static async Task<T> UntilAsync<T>(Func<Task<T?>> read, CancellationToken ct, string never)
    {
        while (!ct.IsCancellationRequested)
        {
            if (await read() is T found)
            {
                return found;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }

        Assert.Fail(never);

        throw new InvalidOperationException(never);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }

    /// <summary>Everything one test holds, disposed in the order the plugin's own shutdown would.</summary>
    private sealed class Run(
        TorrentDownloaderPlugin plugin,
        FakePluginContext context,
        FakeHttp http,
        GrabRepository grabs,
        TorrentMetadata torrent,
        BittorrentEngine? seeder,
        string incomplete) : IDisposable
    {
        public TorrentDownloaderPlugin Plugin => plugin;

        public FakeHttp Http => http;

        public GrabRepository Grabs => grabs;

        public TorrentMetadata Torrent => torrent;

        public string Hash => torrent.InfoHash;

        public string Incomplete => incomplete;

        /// <summary>Every line the plugin logged.</summary>
        public IReadOnlyList<string> Said => context.Log.Lines;

        /// <summary>The one episode's outcome, or what the plugin said when it decided nothing.</summary>
        public EpisodeOutcome Outcome()
        {
            if (plugin.LastCycle is [EpisodeOutcome only])
            {
                return only;
            }

            Assert.Fail(
                $"the run decided about {plugin.LastCycle.Count} episodes, not one. The plugin said:\n"
                + string.Join("\n", context.Log.Lines)
                + "\nand the journal holds:\n"
                + string.Join("\n", plugin.Journal.Snapshot().History.Select(entry => $"{entry.Stage} {entry.Subject}: {entry.Detail}")));

            throw new InvalidOperationException();
        }

        /// <summary>A delivery of 0.8.1 or earlier: done, and with nothing written down about where its name came from.</summary>
        public async Task Delivered(string release, EpisodeKey episode, CancellationToken ct)
        {
            await grabs.RecordAsync(
                episode,
                "South Park",
                release,
                "The Pirate Bay",
                Hash,
                "magnet:?xt=urn:btih:" + Hash,
                [episode],
                new DateTimeOffset(2026, 10, 10, 12, 0, 0, TimeSpan.Zero),
                ct);

            await grabs.StateAsync(Hash, GrabState.Done, ct);
        }

        public void Dispose()
        {
            plugin.Dispose();
            seeder?.Dispose();
        }
    }
}
