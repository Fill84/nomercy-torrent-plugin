using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
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

    /// <summary>What the plugin <see cref="AStagingThatFailsAsync"/> built has said.</summary>
    private CapturingLogger _said = new();

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
            Encodes = new FakeEncoder(),
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
    /// <para>
    /// <strong>And the refusal may come before the grab exists.</strong> The
    /// client takes the torrent before the row is written, and a plugin on
    /// contract 12 starts itself, so a pass can run in that gap: it finds a
    /// torrent the store does not know, stops it, and the row written a moment
    /// later answers for nothing the client holds. On 22 September 2026 this
    /// test timed out that way under the full suite, once. Adding by hand asks
    /// for a pass once the row is written, which puts the torrent back and lets
    /// the refusal reach a grab that can take it.
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
            Encodes = new FakeEncoder(),
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
    /// The start is the one a plugin on contract 12 gives itself off the
    /// <c>Initialize</c> thread: the folders are set, the grab is in the store,
    /// its file is whole on disk, and all of it was there before the plugin was
    /// loaded. Nothing else is done — no page, no Run, no cycle due.
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

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Encodes = new FakeEncoder(),
        };

        // What the server kept when it stopped, written before the plugin is
        // loaded, because that is when it was written. This test used to load
        // the plugin, save the folders and only then write the grab — and saving
        // the folders is itself a start, so the grab raced the very pass it was
        // there for. Under the full suite on 15 October 2026 that pass read the
        // store first, found nothing to put back, and nothing looked again.
        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await new SettingsStore(context.Configuration, context.Secrets)
            .SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        // A grab still downloading.
        await new GrabRepository(database).RecordAsync(
            new(0, 0, 0),
            string.Empty,
            name,
            "by hand",
            hash,
            torrent,
            [],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // And a cycle that finished a moment ago, so none is due. A cycle
        // starting looks at the transfers too, and with no cycle on record one
        // starts at once: the start's own pass could then go and this test would
        // not notice.
        await new CadenceRepository(database).RecordFinishedAsync(
            Cadences.Name, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(Directory.GetFiles(intake));

        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(context);

        // Nothing else happens: the plugin starts itself, and the client says
        // the download is whole as it opens.
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (Directory.GetFiles(intake).Length == 0 && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        Assert.True(
            Directory.GetFiles(intake).Length > 0,
            "the plugin started and the download that finished while the server was down was never staged.");
    }

    /// <remarks>
    /// <para>
    /// <c>Initialize</c> does no I/O and never throws, because the server is still
    /// coming up while it runs and an exception there marks the plugin
    /// malfunctioned before it has a page on which to say why. What a start owes
    /// runs off that thread, on its own — a plugin on contract 12 is told nothing
    /// by the server once it is loaded, so there is nothing to wait for.
    /// </para>
    /// <para>
    /// Proved on a plugin nobody has configured: <c>Initialize</c> returns, and
    /// the start — which is what says that no folders are configured — is heard
    /// from afterwards with nothing else having happened. Nothing is on the disk,
    /// because a start with nowhere to put a download owes nothing yet.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task InitialiseReturnsAndTheStartRunsOnItsOwn()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new() { DataFolderPath = _folder };

        plugin.Initialize(context);

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (!context.Log.Lines.Any(line => line.Contains("No folders are configured", StringComparison.Ordinal))
               && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.Contains(context.Log.Lines, line => line.Contains("No folders are configured", StringComparison.Ordinal));

        // Its own log is the one thing it writes: the line just heard went to the plugin's log file as well
        // (GitHub issue #7). Nothing else — no database, no browser, no download.
        Assert.True(
            !Directory.Exists(_folder) || Directory.EnumerateFileSystemEntries(_folder).All(entry => Path.GetFileName(entry) == "logs"),
            "a plugin with no folders configured touched the disk.");
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
            Encodes = new FakeEncoder(),
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
    /// <strong>A start closes a grab whose encode the server says finished.</strong>
    /// What the store held when the server stopped: a grab handed to the
    /// encoder, with the job the server named for it. The episode is in the
    /// library now, and asked by that id the server says the job is done. The
    /// plugin starts, its first pass asks, and the staged copy is taken away and
    /// the grab marked done — with nobody pressing anything.
    /// </para>
    /// <para>
    /// Asked, not heard: until contract 12 the server's own encoding event drove
    /// this, and a plugin on 12 has no bus. The job id kept with the grab is the
    /// one handle left, which is why it is written before the plugin starts here
    /// — a restart is exactly when memory would have lost it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStartClosesAGrabWhoseEncodeTheServerSaysFinished()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        string staged = Path.Combine(intake, "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv");
        await File.WriteAllTextAsync(staged, "the copy the encoder read");

        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        EpisodeKey episode = new(41, 3, 6);

        // What the store held when the server stopped.
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);
        GrabRepository grabs = new(database);

        await grabs.RecordAsync(
            episode, "Silo", "Silo.2023.S03E06.1080p.WEB.H264-CAKES", "1337x", hash,
            $"magnet:?xt=urn:btih:{hash}", [episode], DateTimeOffset.UtcNow, CancellationToken.None);
        await grabs.StagedAsync(hash, [staged], CancellationToken.None);
        await grabs.EncodeAsync(hash, episode, "01KZGKX2G0966V80H26EKGG5T1", CancellationToken.None);
        await grabs.StateAsync(hash, GrabState.Dispatched, CancellationToken.None);

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
            Encodes = new FakeEncoder(),
            Jobs = new FakeJobs().Says("01KZGKX2G0966V80H26EKGG5T1", PluginJobState.Finished),
        };

        plugin.Initialize(context);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Assert.Equal(GrabState.Done, await UntilDoneAsync(grabs, hash));
        Assert.False(File.Exists(staged), "the copy the encoder had finished with was left in the intake folder.");
        Assert.Contains("01KZGKX2G0966V80H26EKGG5T1", context.Jobs.Asked);
    }

    /// <remarks>
    /// <para>
    /// <strong>A start closes a grab whose episode has arrived, though nothing
    /// can be asked.</strong> A grab dispatched by a server that named no job,
    /// or before this plugin kept the id, has nothing to ask about — and an
    /// encode taken out of the queue by hand, or one that ended with nothing to
    /// encode, would have nothing to say. The owner's ruling of 14 September
    /// 2026 is that the library decides: the episode is there, so the grab is
    /// done.
    /// </para>
    /// <para>
    /// No jobs facade here at all: only the start, and the episode in the library.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStartClosesAGrabWhoseEpisodeHasArrivedThoughNothingCanBeAsked()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        string staged = Path.Combine(intake, "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv");
        await File.WriteAllTextAsync(staged, "the copy the encoder read");

        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        EpisodeKey episode = new(41, 3, 6);

        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);
        GrabRepository grabs = new(database);

        await grabs.RecordAsync(
            episode, "Silo", "Silo.2023.S03E06.1080p.WEB.H264-CAKES", "1337x", hash,
            $"magnet:?xt=urn:btih:{hash}", [episode], DateTimeOffset.UtcNow, CancellationToken.None);
        await grabs.StagedAsync(hash, [staged], CancellationToken.None);
        await grabs.StateAsync(hash, GrabState.Dispatched, CancellationToken.None);

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
            Encodes = new FakeEncoder(),
        };

        plugin.Initialize(context);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Assert.Equal(GrabState.Done, await UntilDoneAsync(grabs, hash));
        Assert.False(File.Exists(staged), "the copy the encoder had finished with was left in the intake folder.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A grab waiting on an encode is asked about again on its own.</strong>
    /// On 22 September 2026 Lioness S03E08 was encoded, registered in the
    /// library at 12:41 and still read "encoding" on the Downloads page an hour
    /// later: a plugin on contract 12 hears no encode end, and nothing started a
    /// pass — a pass ran when a download finished, on a start, and on nothing
    /// else. So while a grab waits on an encode, a pass comes round by itself,
    /// and the one that finds the episode in the library closes the grab.
    /// </para>
    /// <para>
    /// Here the first pass finds the job running and the episode absent; then
    /// the server says the job finished and the library has the episode, and
    /// nobody presses anything.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrabWaitingOnAnEncodeIsAskedAboutAgainOnItsOwn()
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        string staged = Path.Combine(intake, "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv");
        await File.WriteAllTextAsync(staged, "the copy the encoder is reading");

        const string hash = "0123456789ABCDEF0123456789ABCDEF01234567";
        EpisodeKey episode = new(41, 3, 6);

        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);
        GrabRepository grabs = new(database);

        await grabs.RecordAsync(
            episode, "Silo", "Silo.2023.S03E06.1080p.WEB.H264-CAKES", "1337x", hash,
            $"magnet:?xt=urn:btih:{hash}", [episode], DateTimeOffset.UtcNow, CancellationToken.None);
        await grabs.StagedAsync(hash, [staged], CancellationToken.None);
        await grabs.EncodeAsync(hash, episode, "01KZGKX2G0966V80H26EKGG5T1", CancellationToken.None);
        await grabs.StateAsync(hash, GrabState.Dispatched, CancellationToken.None);

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        FakeJobs jobs = new FakeJobs().Says("01KZGKX2G0966V80H26EKGG5T1", PluginJobState.Running);

        using TorrentDownloaderPlugin plugin = new()
        {
            // Seconds rather than the minute a server gets: what is under test
            // is that a pass comes round at all, not how long the owner waits.
            LookAgainInterval = TimeSpan.FromMilliseconds(200),
        };

        plugin.Initialize(new FakePluginContext
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Encodes = new FakeEncoder(),
            Jobs = jobs,
        });

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
        settings.Client.ListenPort = 0;

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        // The first pass asks and finds the job running; the grab waits.
        DateTimeOffset askedBy = DateTimeOffset.UtcNow + Hang.Limit;

        while (jobs.Asked.Count == 0 && DateTimeOffset.UtcNow < askedBy)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        Assert.NotEmpty(jobs.Asked);
        Assert.Equal(GrabState.Dispatched, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // The encode ends and the library gains the episode. Nobody presses anything.
        jobs.Says("01KZGKX2G0966V80H26EKGG5T1", PluginJobState.Finished);
        shelves.Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: true);

        Assert.Equal(GrabState.Done, await UntilDoneAsync(grabs, hash));
        Assert.True(jobs.Asked.Count >= 2, "the grab was asked about once and never again.");
        Assert.False(File.Exists(staged), "the copy the encoder had finished with was left in the intake folder.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A finished download whose staging failed is looked at again by itself.</strong> GitHub issue
    /// #3: a download completed and never reached the intake folder. The client says a download finished
    /// once, and a pass ran on that and on nothing else that was still to come — so a staging that failed on
    /// that pass, for a reason that went away a moment later, left the download in the incomplete folder until
    /// something unrelated started a pass.
    /// </para>
    /// <para>
    /// Here the first staging is refused — a folder in the intake folder carries the episode's name — and then
    /// the folder goes. Nothing else happens: no completion, no run, no page. The pass that comes round while
    /// a finished download waits to be staged is what stages it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadWhoseStagingFailedIsStagedAgainWithoutAnythingAsking()
    {
        (TorrentDownloaderPlugin plugin, string staged) = await AStagingThatFailsAsync(

            // Seconds rather than the minute a server gets: what is under test is that a pass comes round
            // at all, not how long the owner waits.
            TimeSpan.FromMilliseconds(200));

        using (plugin)
        {
            // What was in the way is gone. Nobody presses anything.
            Directory.Delete(staged);

            DateTimeOffset cleared = DateTimeOffset.UtcNow;

            Assert.True(
                await UntilAsync(() => File.Exists(staged)),
                "a finished download's staging failed once and nothing ever looked at it again.");

            // On the look the plugin was given and not on a minute of its own: a failed staging rests until
            // the look comes round, and the look is the one retry.
            Assert.True(
                DateTimeOffset.UtcNow - cleared < TimeSpan.FromSeconds(30),
                "the failed staging was tried again on a clock of its own, not on the plugin's look.");
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A run looks at what the client holds before it does anything else.</strong> The cadence is the
    /// safety net under every event: whatever a lost or failed pass left behind, the next run's start finds.
    /// </para>
    /// <para>
    /// The pass that comes round by itself is put out of reach here — an hour away — so what stages the
    /// download is the run the owner starts, and nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARunStagesAFinishedDownloadWhoseStagingFailedBefore()
    {
        (TorrentDownloaderPlugin plugin, string staged) = await AStagingThatFailsAsync(TimeSpan.FromHours(1));

        using (plugin)
        {
            Directory.Delete(staged);

            await plugin.RunCycleAsync(CancellationToken.None);

            Assert.True(
                await UntilAsync(() => File.Exists(staged)),
                "a run started and the finished download waiting to be staged was not looked at.");
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A staging that failed waits for the look, not for the client.</strong> The torrent is let go of
    /// so its file can be moved, the move fails, and the client is handed it back; it opens it whole from what
    /// it kept and says it finished, and that started the next pass at once — which failed the same way, in a
    /// loop paced by how fast the client reopens a torrent, each round of which may copy gigabytes.
    /// </para>
    /// <para>
    /// Here, through the plugin and a real client, the look is an hour away and the way stays blocked: in the
    /// seconds after the first refusal, staging is not tried again.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStagingThatFailedIsNotTriedAgainUntilTheLookComesRound()
    {
        (TorrentDownloaderPlugin plugin, _) = await AStagingThatFailsAsync(
            TimeSpan.FromHours(1),
            LogLevel.Debug,
            isPrivate: false);

        using (plugin)
        {
            // Long enough for a client reopening a 64 KB torrent to say it finished many times over.
            await Task.Delay(TimeSpan.FromSeconds(3));

            // The plugin's own file, which keeps the Debug line each repeat is written at.
            plugin.Log.Flush();

            Assert.Single(
                LogFiles.Lines(plugin.Log.Folder),
                line => line.Contains("could not be staged", StringComparison.Ordinal));
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>On one volume a finished download is moved into the intake folder.</strong> GitHub issue #3
    /// asks for a finished download to reach the intake folder on the same volume and on another. Here,
    /// through the plugin and a real client, on one: the episode arrives whole, the download is gone, and the
    /// file is the file it was — a copy is made now, a moved file keeps the moment it was made.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadOnTheSameVolumeIsMovedIntoTheIntakeFolder()
    {
        DateTime made = new(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        (string download, string staged, byte[] content) =
            await FinishThroughThePluginAsync(Path.Combine(_folder, "incomplete"), made);

        Assert.Equal(content, await File.ReadAllBytesAsync(staged));
        Assert.False(File.Exists(download), "the download was left in the incomplete folder.");
        Assert.Equal(made, File.GetCreationTimeUtc(staged));
    }

    /// <remarks>
    /// <para>
    /// <strong>On another volume a finished download reaches the intake folder too.</strong> The download
    /// folder and the intake folder are often two disks — the download lands on the fast one, the library
    /// lives on the big one — and a move between two file systems is a copy the file system makes. Here the
    /// incomplete folder is on a second file system for real: another drive on Windows, <c>/dev/shm</c> on
    /// Linux, which is a tmpfs.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadOnAnotherVolumeReachesTheIntakeFolder()
    {
        string incomplete = Path.Combine(Volumes.Another(), "nomercy-chain-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            (string download, string staged, byte[] content) = await FinishThroughThePluginAsync(incomplete, made: null);

            Assert.Equal(content, await File.ReadAllBytesAsync(staged));
            Assert.False(File.Exists(download), "the download was left in the incomplete folder.");
        }
        finally
        {
            TemporaryFolder.Forget(incomplete);
        }
    }

    /// <summary>
    /// A download added by hand, whole on disk in <paramref name="incomplete"/>, taken through the plugin and a
    /// real client until the episode is in the intake folder.
    /// </summary>
    /// <param name="incomplete">The incomplete folder, on whichever volume the test wants.</param>
    /// <param name="made">When the download says it was made, so a test can tell a move from a copy.</param>
    /// <returns>Where the download was, where the episode is, and its bytes.</returns>
    private async Task<(string Download, string Staged, byte[] Content)> FinishThroughThePluginAsync(
        string incomplete,
        DateTime? made)
    {
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        const string name = "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv";

        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        string download = Path.Combine(incomplete, name);
        await File.WriteAllBytesAsync(download, content);

        if (made is DateTime moment)
        {
            File.SetCreationTimeUtc(download, moment);
        }

        string torrent = Path.Combine(_folder, "episode.torrent");
        await File.WriteAllBytesAsync(torrent, Torrent(name, content, pieceLength: 16 * 1024));

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        string staged = Path.Combine(
            intake,
            EpisodeName.For("Silo", 2023, new(41, 3, 6), ReleaseName.Parse(name).Resolution, ".mkv"));

        using (TorrentDownloaderPlugin plugin = new())
        {
            plugin.Initialize(new FakePluginContext
            {
                DataFolderPath = _folder,
                Shelves = shelves,
                Permits = new FakeGrants(),
                Encodes = new FakeEncoder(),
            });

            // A cycle finished a moment ago, so the cadence starts none and nothing here reads a site.
            _ = await plugin.EpisodesAsync(CancellationToken.None);
            await new CadenceRepository(new Store(_folder)).RecordFinishedAsync(
                JobNames.Cycle, DateTimeOffset.UtcNow, CancellationToken.None);

            Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake };
            settings.Client.ListenPort = 0;

            SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
            Assert.True(saved.Saved, string.Join("; ", saved.Errors));

            (string? hash, string? refusal) = await plugin.AddTorrentAsync(torrent, CancellationToken.None);
            Assert.True(hash is not null, refusal);

            Assert.True(
                await UntilAsync(() => File.Exists(staged)),
                "the client said the download finished and it never reached the intake folder.");
        }

        return (download, staged, content);
    }

    /// <summary>
    /// A plugin holding a finished download whose first staging was refused, and where the episode would go.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the store held when the server stopped: a grab of a private torrent, whole on disk. The plugin
    /// starts, puts the torrent back in the client, and the client says it is finished. Private, so it seeds
    /// and is copied rather than let go of: a torrent let go of whose staging fails is added back to the
    /// client, which says once more that it is finished, and that is a second look of its own.
    /// </para>
    /// <para>
    /// The staging is refused because a folder carries the episode's name. A cycle finished a moment ago is
    /// written down first, so the cadence starts none, and the show is switched on before the folders are set,
    /// because a pass cancels a grab whose show is off.
    /// </para>
    /// </remarks>
    /// <param name="lookAgain">How long until the pass comes round by itself while something waits.</param>
    /// <param name="level">What the plugin's log keeps, so a test can count the quiet repeats too.</param>
    /// <param name="isPrivate">
    /// False for a public torrent, which nothing seeds, so it is let go of and its file moved — and, the move
    /// refused, handed back to the client.
    /// </param>
    private async Task<(TorrentDownloaderPlugin Plugin, string Staged)> AStagingThatFailsAsync(
        TimeSpan lookAgain,
        LogLevel level = LogLevel.Information,
        bool isPrivate = true)
    {
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        Directory.CreateDirectory(incomplete);
        Directory.CreateDirectory(intake);

        const string name = "Silo.2023.S03E06.1080p.WEB.H264-CAKES.mkv";
        EpisodeKey episode = new(41, 3, 6);

        byte[] content = RandomNumberGenerator.GetBytes(64 * 1024);
        await File.WriteAllBytesAsync(Path.Combine(incomplete, name), content);

        byte[] file = Torrent(name, content, pieceLength: 16 * 1024, isPrivate: isPrivate);
        string torrent = Path.Combine(_folder, "episode.torrent");
        await File.WriteAllBytesAsync(torrent, file);

        FakeLibraryQuery shelves = new FakeLibraryQuery()
            .Library("01HQ5W4AVF30N10RT6XCF6AJHM", "Series", "tv")
            .Show(41, "Silo", "01HQ5W4AVF30N10RT6XCF6AJHM", 2023, folder: "/Silo.(2023)")
            .Episode(41, 3, 6, "The Getaway", new DateTime(2020, 1, 1), hasFile: false)
            .Episode(41, 1, 1, "Freedom Day", new DateTime(2019, 1, 1), hasFile: true);

        TorrentDownloaderPlugin plugin = new() { LookAgainInterval = lookAgain };

        FakePluginContext context = new()
        {
            DataFolderPath = _folder,
            Shelves = shelves,
            Permits = new FakeGrants(),
            Encodes = new FakeEncoder(),
        };

        plugin.Initialize(context);
        _said = context.Log;

        _ = await plugin.EpisodesAsync(CancellationToken.None);
        await new CadenceRepository(new Store(_folder)).RecordFinishedAsync(
            JobNames.Cycle, DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Empty(await plugin.SaveShowSettingsAsync(
            41,
            new Dictionary<string, string?> { ["switchedOn"] = "true", ["quality"] = "1080p" },
            CancellationToken.None));

        await (await plugin.GrabsAsync(CancellationToken.None)).RecordAsync(
            episode,
            "Silo",
            name,
            "1337x",
            TorrentMetadata.Read(file).InfoHash,
            torrent,
            [episode],
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        // Where the episode goes, taken by a folder: the copy cannot be given the episode's name.
        string staged = Path.Combine(
            intake,
            EpisodeName.For("Silo", 2023, episode, ReleaseName.Parse(name).Resolution, ".mkv"));

        Directory.CreateDirectory(staged);

        Settings settings = new() { IncompleteFolder = incomplete, IntakeFolder = intake, LogLevel = level };
        settings.Client.ListenPort = 0;

        // So a run reads nobody: this touches no network.
        foreach (string source in System.Text.Json.JsonDocument
                     .Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sources.json")))
                     .RootElement.GetProperty("sources").EnumerateArray()
                     .Select(one => one.GetProperty("name").GetString()!))
        {
            settings.DisabledDefaultSources.Add(source);
        }

        SaveResult saved = await plugin.Settings.SaveAsync(settings, CancellationToken.None);
        Assert.True(saved.Saved, string.Join("; ", saved.Errors));

        Assert.True(
            await UntilAsync(() => context.Log.Entries.Any(entry =>
                entry.Level == LogLevel.Warning && entry.Line.Contains("could not be staged", StringComparison.Ordinal))),
            "the first staging was never tried, so nothing here was refused.");

        Assert.False(File.Exists(staged));

        return (plugin, staged);
    }

    /// <summary>Whether a condition came true before <see cref="Hang.Limit"/> ran out.</summary>
    private static async Task<bool> UntilAsync(Func<bool> done)
    {
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

        while (!done() && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
        }

        return done();
    }

    /// <summary>The grab's state once it is done, or whatever it is when the limit runs out.</summary>
    private static async Task<GrabState> UntilDoneAsync(GrabRepository grabs, string hash)
    {
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;
        GrabState state = GrabState.Dispatched;

        while (state != GrabState.Done && DateTimeOffset.UtcNow < giveUpAt)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));

            state = (await grabs.EveryAsync(CancellationToken.None))
                .Single(one => string.Equals(one.InfoHash, hash, StringComparison.OrdinalIgnoreCase))
                .State;
        }

        return state;
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

        _ = await plugin.GetViewAsync(Requests.View("/downloads"), CancellationToken.None);

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
    /// <param name="name">What the torrent calls its one file.</param>
    /// <param name="content">The file's bytes.</param>
    /// <param name="pieceLength">How the bytes are cut into pieces.</param>
    /// <param name="isPrivate">
    /// A private torrent seeds until its own rules are met, so a finished one is copied and stays with the
    /// client rather than being let go of.
    /// </param>
    private static byte[] Torrent(string name, byte[] content, long pieceLength, bool isPrivate = false)
    {
        List<byte> hashes = [];

        for (int at = 0; at < content.Length; at += (int)pieceLength)
        {
            hashes.AddRange(SHA1.HashData(content.AsSpan(at, Math.Min((int)pieceLength, content.Length - at))));
        }

        List<BencodeEntry> info =
        [
            new("length"u8.ToArray(), new BencodeInteger(content.Length)),
            new("name"u8.ToArray(), new BencodeBytes(System.Text.Encoding.UTF8.GetBytes(name))),
            new("piece length"u8.ToArray(), new BencodeInteger(pieceLength)),
            new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),
        ];

        if (isPrivate)
        {
            info.Add(new("private"u8.ToArray(), new BencodeInteger(1)));
        }

        return Bencode.Write(new BencodeDictionary(
        [
            new("info"u8.ToArray(), new BencodeDictionary(info)),
        ]));
    }
}
