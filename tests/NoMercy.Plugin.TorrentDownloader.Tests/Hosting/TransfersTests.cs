using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
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
    private const string TelevisionLibrary = "01KZGKX2G0966V80H26EKGG5T0";

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
        server.Files.Matches = [(Staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.False(File.Exists(episode), "The download was left where it was.");
        Assert.NotNull(server.Dispatcher.Job);
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent added by hand is an episode like any other.</strong>
    /// docs/08-ui.md § Actions: <c>AddTorrent</c> still runs the finished file
    /// through staging and the encode dispatch.
    /// </para>
    /// <para>
    /// It is recorded covering no episode, and staging is handed the episodes —
    /// so with none it chose no file, moved nothing and dispatched nothing. A
    /// season pack pasted in by hand downloaded in full and stopped there: on
    /// 30 August 2026, 37 GB of Dark Matter sat complete in the download folder
    /// with nothing in the plugin able to move it. What it holds is read out of
    /// its own file names instead, and written down, because every step after
    /// staging reads the episodes back out of the store.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentAddedByHandIsStagedAndDispatchedLikeAnyOther()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.NotNull(server.Dispatcher.Job);

        // And it says which episode it turned out to be, in the store rather
        // than only in this tick: the steps that mark the episode arrived and
        // clear the torrent read it from there.
        StoredDownload stored = Assert.Single(await grabs.EveryAsync(CancellationToken.None));

        Assert.Equal([Episode], stored.Covers);
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

    /// <remarks>
    /// <para>
    /// <strong>An encode that was refused is asked for again.</strong> A grab
    /// used to be marked done the moment its file was copied, whether or not
    /// the encode was ever taken — so a refusal was forgotten and the episode
    /// sat in the intake folder for ever, with the plugin never coming back to
    /// it. Three of the owner's were found there on 24 August 2026.
    /// </para>
    /// <para>
    /// And it is asked for without copying anything again: the file is already
    /// where it belongs, and re-staging it every minute is gigabytes of
    /// nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeThatWasRefusedIsAskedForAgainWithoutStagingTwice()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);
        string staged = Staged;

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        // The server knows nothing about it yet, so the encode is refused.
        server.Files.Matches = [];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(staged), "It was never staged.");
        Assert.Null(server.Dispatcher.Job);

        StoredDownload waiting = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Staged, waiting.State);
        Assert.Equal(staged, waiting.StagedPath);

        // The download is gone from the incomplete folder, so a tick that tried
        // to stage again would have nothing to copy and would say so.
        Assert.False(File.Exists(episode));

        // Now the server knows it, and the next tick asks again.
        server.Files.Matches = [(staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.NotNull(server.Dispatcher.Job);
        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);
    }

    /// <remarks>
    /// <para>
    /// <strong>The library having the episode is the end of it.</strong> That
    /// is the only proof the encode finished — the plugin cannot see the
    /// server's queue — and it is what everything was for.
    /// </para>
    /// <para>
    /// Then the copy in the intake folder goes, and the torrent and what it
    /// downloaded with it. Left behind they are two more copies of an episode
    /// the owner already has, re-checked on every start for ever.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhenTheLibraryHasTheEpisodeEveryCopyOfItIsDeleted()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);
        string staged = Staged;

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // Still there while the encoder is working, because the library does
        // not have the episode yet.
        Assert.True(File.Exists(staged));

        // Finished rather than seeding: a public torrent stops the moment it is
        // complete, because nothing is ever uploaded on a public swarm. One
        // that is still seeding is left alone, which is its own test.
        engine.Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        await Transfers(engine, grabs, server, encoded: true)
            .TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.False(File.Exists(staged), "The staged copy was left behind.");

        (string InfoHash, bool DeleteFiles) removed = Assert.Single(engine.Removed);

        Assert.Equal(Hash, removed.InfoHash);
        Assert.True(removed.DeleteFiles, "The download was left on the disk.");
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent still seeding is not cleared up.</strong> The library
    /// having the episode says the encode finished; it says nothing about what
    /// the torrent still owes. A private torrent seeds to the owner's ratio or
    /// hours, and the library can have the episode long before either — so
    /// deleting then costs the owner exactly the account the seeding rules were
    /// written to protect.
    /// </para>
    /// <para>
    /// Nothing is lost by waiting: the episode is already in the library. The
    /// tick after the seed limit stops it finishes the job.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentThatIsStillSeedingIsLeftAlone()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);
        string staged = Staged;

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        // Finished() is seeding, and the library now has the episode.
        await Transfers(engine, grabs, server, encoded: true)
            .TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(staged), "The staged copy went while the torrent was still seeding.");
        Assert.Empty(engine.Removed);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // The seed limit stops it, and the next tick clears up.
        engine.Holding(Finished() with { State = TorrentState.Finished }, new TorrentFile(Path.GetFileName(episode), 900_000_000));

        await Transfers(engine, grabs, server, encoded: true)
            .TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.False(File.Exists(staged));
        Assert.Single(engine.Removed);
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>A file in the intake folder that nothing is waiting on is still
    /// dispatched.</strong> Before a grab recorded where it staged its episode,
    /// it was marked done the moment the file was copied — whether or not the
    /// encode had been taken. Three of the owner's episodes were left in the
    /// intake folder that way, with nothing that would ever come back to them.
    /// </para>
    /// <para>
    /// So every tick looks at what is really in the folder, and anything no
    /// open grab is waiting on is matched to the grab that put it there and
    /// asked for again. It then joins the ordinary path: dispatched, then the
    /// library, then deleted.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEpisodeLeftInTheIntakeFolderIsDispatchedAnyway()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        // Staged and marked done by a version that asked for no encode — and
        // under the release's name, because that is what that version wrote.
        Directory.CreateDirectory(Intake);

        string staged = Path.Combine(Intake, "Silo.S03E06.1080p.WEB.H264-CAKES.mkv");

        await File.WriteAllBytesAsync(staged, new byte[2048]);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        FakeProvider server = Server();

        server.Files.Matches = [(staged, "4417")];

        await Transfers(new StandingEngine(), grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.NotNull(server.Dispatcher.Job);

        // And it is being waited on now, so the copies go when the library has
        // it rather than being left for ever.
        StoredDownload waiting = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Dispatched, waiting.State);
        Assert.Equal(staged, waiting.StagedPath);
    }

    /// <remarks>
    /// <para>
    /// <strong>A staged episode whose file has gone is not delivered.</strong>
    /// The encode was never taken and the file is no longer there to offer, so
    /// there is nothing left to wait for — and waiting is what it used to do,
    /// silently and for ever, with the episode neither in the library nor being
    /// looked for.
    /// </para>
    /// <para>
    /// It goes back to missing so the next cycle can find it again. The plugin
    /// cannot tell whether the owner moved it or something deleted it, and
    /// either way the library does not have it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStagedEpisodeWhoseFileHasGoneIsLookedForAgain()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        // Staged, the encode refused, and then the file taken away.
        await grabs.StagedAsync(Hash, Staged, CancellationToken.None);

        await Transfers(new StandingEngine(), grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>A grab for a show the owner does not have is cancelled and its
    /// download deleted.</strong> On 24 August 2026 the library rule was
    /// widened to every show in a library, and within the hour the plugin was
    /// on 479 grabs: the server keeps rows for shows nobody asked for, and
    /// Family Guy alone claimed 456 missing episodes.
    /// </para>
    /// <para>
    /// Putting the rule back stops more being made. It does nothing about the
    /// ones already running, and leaving those to finish would fill the owner's
    /// disk with a show they have never watched. So a grab whose show is not
    /// one they have goes, and takes its bytes with it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrabForAShowTheOwnerDoesNotHaveIsCancelled()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        StandingEngine engine = new StandingEngine().Holding(Downloading());

        // The show has no episode on disk, so it is not one the owner has.
        await Transfers(engine, grabs, Server(), owned: false)
            .TickAsync(Incomplete, Intake, CancellationToken.None);

        (string InfoHash, bool DeleteFiles) removed = Assert.Single(engine.Removed);

        Assert.Equal(Hash, removed.InfoHash);
        Assert.True(removed.DeleteFiles, "Its download was left on the disk.");
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <summary>A torrent that is still going, so nothing else in the tick acts on it.</summary>
    private static TorrentStatus Downloading()
    {
        return Finished() with { State = TorrentState.Downloading, BytesDone = 10_000 };
    }

    /// <remarks>
    /// <para>
    /// <strong>Eight rows of one torrent are one torrent.</strong> Every cycle
    /// used to record a fresh grab for an episode it was already downloading,
    /// because an episode stays missing until the library has a file for it. So
    /// one release ended up with eight rows under one info hash — and every
    /// step here walked rows.
    /// </para>
    /// <para>
    /// Eight encode jobs for one file, on every tick. The owner's History page
    /// showed Lucky S01E07 dispatched five times inside twenty seconds, and
    /// carried 167 dispatches for a handful of episodes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ManyRowsOfOneTorrentAreDispatchedOnce()
    {
        GrabRepository grabs = await Grabs();

        // The same torrent, recorded three times, as the cycle used to.
        await Grabbed(grabs);
        await Grabbed(grabs);
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Dispatcher.Dispatches);
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode that never arrives is said, not waited on.</strong>
    /// The library having the episode is the only proof the encode finished,
    /// and a job that failed looks exactly like one still running: both are
    /// "the library does not have it yet", for ever.
    /// </para>
    /// <para>
    /// So after long enough it is given up on and the episode goes back to
    /// missing, with the reason on the History page. It is not dispatched a
    /// second time: a second job for a file the encoder may still be working on
    /// is the one thing worse than waiting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeTheServerSaysFailedIsGivenUpOnAtOnceAndForItsOwnReason()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        // A server that names the job it queued, and then says it died.
        RecordingEncoder encoder = new() { JobId = "01KZGKX2G0966V80H26EKGG5T1" };
        SayingJobs jobs = new(new(EncodeJobState.Failed, "the source file has no audio stream"));

        Transfers transfers = new(
            engine,
            grabs,
            new HostLibrary(new FakeLibraryQuery()
                .Library(TelevisionLibrary, "Television", "tv")
                .Show(41, "Silo", TelevisionLibrary, year: 2023)
                .Episode(41, 3, 6)
                .Episode(41, 1, 1, hasFile: true)),
            new Stager(server.Journal, server.Log),
            encoder,
            server.Journal,
            server.Log,
            clock,
            jobs);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // A minute later, not six hours: the plugin asked rather than waited.
        clock.Advance(TimeSpan.FromMinutes(1));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // In the server's own words, which is the whole point of asking: "it
        // was given up on" tells the owner nothing they can act on.
        Assert.Contains(
            server.Journal.Snapshot().History,
            one => one.Outcome == ActivityOutcome.Failed
                   && (one.Detail ?? string.Empty).Contains("no audio stream", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// A pack added by hand for a show that is in no library is left where it
    /// is, and the History page says so. On 31 August 2026 the owner had 37 GB
    /// of Dark Matter sitting at "finished" and nothing anywhere saying why —
    /// the show is not in their media server at all, so there is no library to
    /// put it in and nothing to name the episodes against.
    /// </para>
    /// <para>
    /// Guessing a library would be worse: files under a name nobody chose, in
    /// somebody else's folder. So it is refused, said out loud once, and asked
    /// again on the next tick — because the answer changes the moment the show
    /// is added.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APackForAShowInNoLibraryIsLeftAloneAndTheReasonIsWrittenDown()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string episode = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        // The library the owner really has, which does not hold this show.
        Transfers transfers = Transfers(engine, grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Left where it is: nothing staged, nothing dispatched, and the grab
        // still open so it is picked up the day the show is added.
        Assert.False(File.Exists(Staged), "It was staged into a library it does not belong to.");
        Assert.Null(server.Dispatcher.Job);
        Assert.NotEmpty(await grabs.OpenAsync(CancellationToken.None));

        // And said once, on the page that keeps it. Twice would bury the page
        // it is written on within a day.
        SkippedPage skipped = await grabs.SkippedAsync(1, 20, CancellationToken.None);

        Assert.Single(skipped.Rows, one => one.Reason.Contains("no library", StringComparison.Ordinal)
                                           || one.Reason.Contains("in a library", StringComparison.Ordinal));
    }

    /// <summary>A server that says the same thing about every job.</summary>
    private sealed class SayingJobs(EncodeJob standing) : IEncodeJobs
    {
        public Task<EncodeJob?> StatusAsync(string jobId, CancellationToken ct)
        {
            return Task.FromResult<EncodeJob?>(standing);
        }
    }

    /// <remarks>
    /// The backstop, for a server that cannot say. It is what every grab
    /// dispatched by the older gateway falls back to, because that one builds
    /// its own job and hands it to a queue that names nothing.
    /// </remarks>
    [Fact]
    public async Task AnEncodeThatNeverArrivesIsGivenUpOn()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);
        string staged = Staged;

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(staged, "4417")];

        // One instance across both ticks, as the plugin keeps one: how long an
        // encode has been waited on is held in memory, and a restart is a good
        // enough reason to start that clock again.
        Transfers transfers = Transfers(engine, grabs, server, clock: clock);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // Six hours later the library still does not have it.
        clock.Advance(TimeSpan.FromHours(6));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // Once, and not dispatched again on the way out.
        Assert.Equal(1, server.Dispatcher.Dispatches);
    }

    /// <remarks>
    /// <para>
    /// <strong>A second copy of an episode is not a second episode.</strong>
    /// The owner's intake folder held two files for Sugar S02E04 — one named
    /// after the uploader's release, one after the same release with the site's
    /// tag on it. A grab records one staged path, so whichever of the two it
    /// was not holding read as a file nothing was waiting on.
    /// </para>
    /// <para>
    /// It was matched back to that same grab, because the release is compared
    /// with the site tag stripped, its staged path was overwritten with the
    /// other file, and an encode was asked for. The next tick found the first
    /// file unwaited-on and did the same in reverse: one dispatch a minute,
    /// alternating between two names, from 15:52 until the owner stopped the
    /// server.
    /// </para>
    /// <para>
    /// A grab that is already staged or dispatched has its file and is being
    /// waited on. Another file that matches it is a copy, never new work.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASecondCopyOfADispatchedEpisodeIsNotDispatchedAgain()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        Transfers transfers = Transfers(engine, grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Dispatcher.Dispatches);

        // The same release under the site's name for it, as a second staging
        // left behind. It is a copy of what was already dispatched.
        File.Copy(
            Staged,
            Path.Combine(Intake, "Silo.S03E06.1080p.WEB.H264-CAKES EZTV.mkv"));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Dispatcher.Dispatches);
    }

    /// <remarks>
    /// <para>
    /// <strong>The intake folder holds what is needed and nothing else.</strong>
    /// The owner's held twenty-two things: five episodes in pairs, six folders
    /// left by 0.3.4 with the tracker's name still on them, and loose files
    /// from releases long since dealt with. Every tick read the lot.
    /// </para>
    /// <para>
    /// Anything a grab is waiting on stays. Everything else goes, folders
    /// included, and each deletion is said. The owner asked for this on
    /// 24 August 2026: the plugin used to leave what it had not put there, and
    /// the folder only ever grew.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task WhatTheIntakeFolderDoesNotNeedIsCleared()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        Transfers transfers = Transfers(engine, grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");

        // A folder 0.3.4 left, a stray from a release long since dealt with,
        // and a second copy of the episode being waited on.
        Directory.CreateDirectory(Path.Combine(Intake, "Rick.and.Morty.S06E03.1080p.WEB.H264-GLHF[TGx]"));
        await File.WriteAllBytesAsync(
            Path.Combine(Intake, "Rick.and.Morty.S06E03.1080p.WEB.H264-GLHF[TGx]", "rick.mkv"),
            new byte[2048]);
        await File.WriteAllBytesAsync(Path.Combine(Intake, "something.nobody.grabbed.mkv"), new byte[2048]);
        File.Copy(Staged, Path.Combine(Intake, "Silo.S03E06.1080p.WEB.H264-CAKES EZTV.mkv"));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "What is waited on was cleared.");
        Assert.Equal([Staged], Directory.EnumerateFileSystemEntries(Intake).Order());
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode dispatched before a restart is asked for once
    /// more.</strong> The owner's queue was empty while eleven grabs sat
    /// waiting on jobs the encoder had already thrown away, and the plugin
    /// cannot see the queue to tell a job that died from one still running.
    /// </para>
    /// <para>
    /// Waiting six hours and then giving up would put the episode back to
    /// missing and download it a second time, with the file already staged the
    /// whole time. So it is asked for again on the first tick after a start,
    /// and then waited on properly.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeDispatchedBeforeARestartIsAskedForOnceMore()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        Directory.CreateDirectory(Intake);
        await File.WriteAllBytesAsync(Staged, new byte[2048]);

        // Dispatched by the run before this one, which is all the store keeps.
        await grabs.StagedAsync(Hash, Staged, CancellationToken.None);
        await grabs.StateAsync(Hash, GrabState.Dispatched, CancellationToken.None);

        FakeProvider server = Server();

        server.Files.Matches = [(Staged, "4417")];

        // A fresh instance, as a restart gives: how long an encode has been
        // waited on is held in memory and nowhere else.
        Transfers transfers = Transfers(new StandingEngine(), grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Dispatcher.Dispatches);

        // Once, and then waited on: this is recovery from a restart, not a
        // second chance on every tick.
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Dispatcher.Dispatches);
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static EpisodeKey Episode => new(41, 3, 6);

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    /// <summary>
    /// Where the episode ends up: its show, its year, its number and its
    /// quality, which is what a staged file is named after. Built from the same
    /// rule the plugin uses rather than written out, so a change to the rule
    /// shows up here as a failure rather than as agreement.
    /// </summary>
    private string Staged => Path.Combine(Intake, EpisodeName.For("Silo", 2023, Episode, "1080p", ".mkv"));

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

    /// <summary>A magnet the owner pasted in, which covers no episode.</summary>
    /// <remarks>
    /// Exactly what <c>AddTorrent</c> writes: no show, no episode, and the
    /// source recorded as "by hand". Claiming an episode nobody chose would put
    /// that episode back to missing if the download failed.
    /// </remarks>
    private static async Task ByHand(GrabRepository grabs)
    {
        await grabs.RecordAsync(
            new(0, 0, 0),
            string.Empty,
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "by hand",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [],
            DateTimeOffset.UtcNow,
            CancellationToken.None);
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

    private static Transfers Transfers(
        StandingEngine engine,
        GrabRepository grabs,
        FakeProvider server,
        bool encoded = false,
        bool owned = true,
        TimeProvider? clock = null,
        IEncodeJobs? jobs = null)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            // A real Ulid, because the server's library id is one and the
            // encode job will not take anything else. "library-tv" made every
            // test here agree with a plugin that could never dispatch.
            .Library(TelevisionLibrary, "Television", "tv")
            // Whether the encode has landed. It is the only thing the plugin
            // can see that says the job finished.
            .Episode(41, 3, 6, hasFile: encoded)
            .Episode(41, 1, 1, hasFile: true);

        // Whether the owner has this show at all, which since media-server #34
        // and #36 is whether it is in a library. A show that is in none is one
        // they removed, or one this plugin was never for.
        if (owned)
        {
            query = query.Show(41, "Silo", TelevisionLibrary, year: 2023);
        }

        return new(
            engine,
            grabs,
            new HostLibrary(query),
            new Stager(server.Journal, server.Log),
            new EncodeDispatch(server, server.Journal, server.Log),
            server.Journal,
            server.Log,
            clock ?? TimeProvider.System,
            jobs);
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
