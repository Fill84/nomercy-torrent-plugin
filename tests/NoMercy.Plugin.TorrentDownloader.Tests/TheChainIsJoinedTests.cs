using System.Security.Cryptography;
using NoMercy.Events.Encoding;
using NoMercy.Events.Library;
using NoMercy.Events.Plugins;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// The links of the chain, joined, through the plugin as the server loads it.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every link had a test and the chain was not joined.</strong>
/// <c>BittorrentEngine.Completed</c> was raised where a download really finished
/// and proved raised; <c>Transfers</c> staged a finished download and was proved
/// to; and nothing in the plugin listened to the one to start the other. The
/// transfers job ticking every minute hid that for as long as it existed, and
/// the slice that removed it removed the only thing joining them — a finished
/// download would have sat in the incomplete folder for ever.
/// </para>
/// <para>
/// So these go through <see cref="TorrentDownloaderPlugin"/>, a real client and a
/// real disk, and never call anything a timer or a page would have called.
/// </para>
/// </remarks>
public sealed class TheChainIsJoinedTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "nomercy-torrent-tests", "chain-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// A download that is whole the moment it is taken on — which is every
    /// staged and dispatched grab after a restart — is staged without anything
    /// asking: no tick, no page, no cycle. The client says it finished and the
    /// plugin takes it from there.
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadIsStagedWithoutAnythingAsking()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        const string name = "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv";

        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(incomplete, name), content);

        string torrent = Path.Combine(_folder, "episode.torrent");
        await File.WriteAllBytesAsync(torrent, Torrent(name, content, pieceLength: 16 * 1024));

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        });

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };

        // Any free port: the owner's own is held by whatever else is running.
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        (string? hash, string? refusal) = await plugin.AddTorrentAsync(torrent, CancellationToken.None);
        Assert.True(hash is not null, refusal);

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (Directory.GetFiles(intake).Length == 0 && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(
            Directory.GetFiles(intake).Length > 0,
            "the client said the download finished and nothing staged it.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent the client gives up on is failed without anything
    /// asking.</strong> The client refuses a torrent with no video file in it the
    /// moment it opens, and until the grab is failed the client goes on holding
    /// it — which holds the cycle open, so maintenance never runs. A transfers
    /// pass ticking every minute used to do the failing; with the cycle driven by
    /// events, nothing did.
    /// </para>
    /// <para>
    /// A book, whole on disk, so nothing is waited for but the refusal itself.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentTheClientGivesUpOnIsFailedWithoutAnythingAsking()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        const string name = "Silo.2023.S03E06.1080p.WEB.H264-CAKES.pdf";

        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(incomplete, name), content);

        string torrent = Path.Combine(_folder, "not-a-video.torrent");
        await File.WriteAllBytesAsync(torrent, Torrent(name, content, pieceLength: 16 * 1024));

        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = new FakeLibraryQuery(),
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        });

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        (string? hash, string? refusal) = await plugin.AddTorrentAsync(torrent, CancellationToken.None);
        Assert.True(hash is not null, refusal);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;
        GrabState state = GrabState.Grabbed;

        while (state != GrabState.Failed && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            state = (await grabs.EveryAsync(CancellationToken.None))
                .Single(one => string.Equals(one.InfoHash, hash, StringComparison.OrdinalIgnoreCase))
                .State;
        }

        Assert.Equal(GrabState.Failed, state);
    }

    /// <remarks>
    /// <para>
    /// <strong>A download that finished while the server was down is staged on
    /// start.</strong> The torrent client is built on first use, and the
    /// transfers job ticking every minute was what used it first — so it was also
    /// what put back every download that had been running. With that job gone,
    /// nothing touched them until somebody opened a page.
    /// </para>
    /// <para>
    /// The start is the server saying the plugin has loaded, which is after
    /// <c>Initialize</c> and is an event: the grab is in the store, its file is
    /// whole on disk, and nothing but that event is published.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ADownloadThatFinishedWhileTheServerWasDownIsStagedOnStart()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        const string name = "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv";

        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(incomplete, name), content);

        byte[] file = Torrent(name, content, pieceLength: 16 * 1024);
        string torrent = Path.Combine(_folder, "episode.torrent");
        await File.WriteAllBytesAsync(torrent, file);

        string hash = TorrentMetadata.Read(file).InfoHash;

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        using TorrentDownloaderPlugin plugin = new();

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        };

        plugin.Initialize(context);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        // What the store held when the server stopped: a grab still downloading.
        await (await plugin.GrabsAsync(CancellationToken.None)).RecordAsync(
            new(0, 0, 0),
            string.Empty,
            name,
            "by hand",
            hash,
            torrent,
            [],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        Assert.Empty(Directory.GetFiles(intake));

        // The server says the plugin has loaded. Nothing else happens.
        await context.Bus.PublishAsync(new PluginLoadedEvent
        {
            PluginId = PluginIdentity.IdText,
            PluginName = PluginIdentity.Name,
            Version = "0.0.0",
        });

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (Directory.GetFiles(intake).Length == 0 && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(
            Directory.GetFiles(intake).Length > 0,
            "the server said the plugin loaded and the download that finished while it was down was never staged.");
    }

    /// <remarks>
    /// <c>Initialize</c> does no I/O, because the server is still coming up while
    /// it runs. What a start owes waits for the server to say the plugin has
    /// loaded — so nothing is on the disk, and nothing is said in the log,
    /// until that is published.
    /// </remarks>
    [Fact]
    public async Task NothingIsStartedUntilTheServerSaysThePluginHasLoaded()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new() { DataFolderPath = _folder };

        plugin.Initialize(context);

        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.False(Directory.Exists(_folder), "the plugin touched the disk before the server said it had loaded.");
        Assert.Empty(context.Log.Lines);
    }

    /// <remarks>
    /// <para>
    /// <strong>The owner's cadence starts a cycle, and nothing else has to.</strong>
    /// The clock is set to the moment the next cycle is due and wakes a trigger
    /// when it comes. A cycle that has never run is due at once — the right
    /// answer on a fresh install — so a plugin that has been configured and told
    /// it has loaded runs one, finishes it and writes that down, with nobody
    /// pressing Run, no library scan and no host tick.
    /// </para>
    /// <para>
    /// The clock was wound by code and proved by nothing: every other test starts
    /// a cycle by hand.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheCadenceStartsACycleWithNothingElseAsking()
    {
        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        using TorrentDownloaderPlugin plugin = new();

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        };

        plugin.Initialize(context);

        Settings settings = new()
        {
            IncompleteFolder = Path.Combine(_folder, "incomplete"),
            IntakeFolder = Path.Combine(_folder, "intake"),
        };

        settings.Client.ListenPort = 0;

        // So the feed reads nobody: this touches no network.
        foreach (string source in System.Text.Json.JsonDocument
                     .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sources.json")))
                     .RootElement.GetProperty("sources").EnumerateArray()
                     .Select(one => one.GetProperty("name").GetString()!))
        {
            settings.DisabledDefaultSources.Add(source);
        }

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        // The database made and migrated before this test reads it from the
        // side. Read while the plugin is still creating it, a second Store opens
        // a half-written file and SQLite throws — which is the test racing the
        // plugin, not anything the plugin does: its own reads all wait on its
        // migration. Migrating starts no cycle.
        _ = await plugin.EpisodesAsync(CancellationToken.None);

        await context.Bus.PublishAsync(new PluginLoadedEvent
        {
            PluginId = PluginIdentity.IdText,
            PluginName = PluginIdentity.Name,
            Version = "0.0.0",
        });

        CadenceRepository cadences = new(new Store(_folder));
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;
        bool finished = false;

        while (!finished && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));

            finished = (await cadences.LastFinishedAsync(CancellationToken.None)).ContainsKey(JobNames.Cycle);
        }

        Assert.True(finished, "the plugin was configured and loaded, and its cadence never started a cycle.");
    }

    /// <remarks>
    /// <para>
    /// <strong>The server saying an encode ended is what closes the grab.</strong>
    /// The grab here was handed to the encoder; the episode is in the library now
    /// and the server has said so. Nothing else happens — no tick, no page — and
    /// the staged copy is taken away and the grab marked done.
    /// </para>
    /// <para>
    /// The test beside this one in <c>TorrentDownloaderPluginTests</c> proves the
    /// pages are told. This proves the work is done, which is the half that
    /// matters to the owner and the half a missing subscription would lose.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheServerSayingAnEncodeEndedClosesTheGrab()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        string staged = Path.Combine(intake, "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv");
        await File.WriteAllTextAsync(staged, "the copy the encoder read");

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")

            // In the library, which is what the encode ending put there.
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: true)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        using TorrentDownloaderPlugin plugin = new();

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        };

        plugin.Initialize(context);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        EpisodeKey episode = new(41, 3, 6);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        await grabs.RecordAsync(
            episode, "Silo", "Silo.2023.S03E06.1080p.WEB.H264-CAKES", "1337x", hash,
            $"magnet:?xt=urn:btih:{hash}", [episode], DateTimeOffset.UtcNow, CancellationToken.None);
        await grabs.StagedAsync(hash, [staged], CancellationToken.None);
        await grabs.StateAsync(hash, GrabState.Dispatched, CancellationToken.None);

        // The server's word, on its own bus, for the episode's row: show 41,
        // season 3, episode 6 is 41306 as FakeLibraryQuery numbers them.
        await context.Bus.PublishAsync(new EncodingCompletedEvent
        {
            JobId = 41306,
            OutputPath = "/data/tv/Silo/Season 3/Silo.S03E06.mkv",
            Duration = TimeSpan.FromMinutes(11),
        });

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;
        GrabState state = GrabState.Dispatched;

        while (state != GrabState.Done && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            state = (await grabs.EveryAsync(CancellationToken.None))
                .Single(one => string.Equals(one.InfoHash, hash, StringComparison.OrdinalIgnoreCase))
                .State;
        }

        Assert.Equal(GrabState.Done, state);
        Assert.False(File.Exists(staged), "the copy the encoder had finished with was left in the intake folder.");
    }

    /// <remarks>
    /// <para>
    /// <strong>The server finishing a scan closes a grab whose episode has
    /// arrived.</strong> An encode taken out of the queue by hand, or one that
    /// ends with nothing to encode, says nothing — so the grab waiting on it would
    /// be waiting for ever. The owner's ruling of 14 September 2026 is that the
    /// library decides, and a finished scan is the server saying the library has
    /// just been read.
    /// </para>
    /// <para>
    /// No encode event here at all: only the scan, and the episode in the library.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheServerFinishingAScanClosesAGrabWhoseEpisodeHasArrived()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        string staged = Path.Combine(intake, "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv");
        await File.WriteAllTextAsync(staged, "the copy the encoder read");

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: true)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        using TorrentDownloaderPlugin plugin = new();

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Container = new FakeProvider(),
        };

        plugin.Initialize(context);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        EpisodeKey episode = new(41, 3, 6);

        GrabRepository grabs = await plugin.GrabsAsync(CancellationToken.None);

        await grabs.RecordAsync(
            episode, "Silo", "Silo.2023.S03E06.1080p.WEB.H264-CAKES", "1337x", hash,
            $"magnet:?xt=urn:btih:{hash}", [episode], DateTimeOffset.UtcNow, CancellationToken.None);
        await grabs.StagedAsync(hash, [staged], CancellationToken.None);
        await grabs.StateAsync(hash, GrabState.Dispatched, CancellationToken.None);

        await context.Bus.PublishAsync(new LibraryScanCompletedEvent
        {
            LibraryId = Ulid.Parse("01HQ5W4AVF30N10RT6XCF6AJHM"),
            LibraryName = "Series",
            ItemsFound = 1,
            Duration = TimeSpan.FromSeconds(2),
        });

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;
        GrabState state = GrabState.Dispatched;

        while (state != GrabState.Done && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            state = (await grabs.EveryAsync(CancellationToken.None))
                .Single(one => string.Equals(one.InfoHash, hash, StringComparison.OrdinalIgnoreCase))
                .State;
        }

        Assert.Equal(GrabState.Done, state);
        Assert.False(File.Exists(staged), "the copy the encoder had finished with was left in the intake folder.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A tab that is closed is found out by the push it does not answer.</strong>
    /// A page is fetched, so somebody is looking. Something changes and the pages
    /// are pushed to — and nothing fetches the view again, which is what a closed
    /// tab looks like from here. Fifteen seconds later the plugin knows nobody is
    /// looking, and stops sampling and pushing for them.
    /// </para>
    /// <para>
    /// Through the plugin as loaded, because the parts were proved and the joins
    /// were not: the push is what has to tell the watch it went out.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APushNobodyAnswersStopsThePluginWatchingForThem()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();

        plugin.Initialize(context);

        _ = await plugin.GetViewAsync(new() { Route = "/downloads" }, CancellationToken.None);

        Assert.True(plugin.Watched);

        // Something a page shows changes.
        plugin.Journal.Failed(ActivityStage.Download, "Silo S03E06", "no peer served its metadata");

        DateTimeOffset pushedBy = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (context.Pushes.Pushes.Count == 0 && DateTimeOffset.UtcNow < pushedBy)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.NotEmpty(context.Pushes.Pushes);

        // And nothing fetches the view again.
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + NoMercy.Plugin.TorrentDownloader.Hosting.Onlookers.Answer + TimeSpan.FromSeconds(5);

        while (plugin.Watched && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(200));
        }

        Assert.False(plugin.Watched, "a push went unanswered and the plugin went on watching for a page that was not there.");
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }

    /// <summary>A real single-file torrent over real bytes.</summary>
    private static byte[] Torrent(string name, byte[] content, long pieceLength)
    {
        List<byte> hashes = [];

        for (int at = 0; at < content.Length; at += (int)pieceLength)
        {
            hashes.AddRange(SHA1.HashData(content.AsSpan(at, Math.Min((int)pieceLength, content.Length - at))));
        }

        return Bencode.Write(new BencodeDictionary(
        [
            new(
                "info"u8.ToArray(),
                new BencodeDictionary(
                [
                    new("length"u8.ToArray(), new BencodeInteger(content.Length)),
                    new("name"u8.ToArray(), new BencodeBytes(System.Text.Encoding.UTF8.GetBytes(name))),
                    new("piece length"u8.ToArray(), new BencodeInteger(pieceLength)),
                    new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),
                ])),
        ]));
    }
}
