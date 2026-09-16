using Microsoft.Extensions.Time.Testing;
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


        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.False(File.Exists(episode), "The download was left where it was.");
        Assert.NotNull(server.Encoder.Job);
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


        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.NotNull(server.Encoder.Job);

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
    /// <para>
    /// <strong>And with the trackers, which are not in the magnet.</strong> An
    /// indexer hands back <c>magnet:?xt=urn:btih:…&amp;dn=…</c> and no
    /// <c>tr=</c> at all; the trackers a torrent runs on come from the indexer
    /// row and from the owner's own list, and are handed over on the first grab
    /// by <c>Grab.TakeAsync</c>. This path handed over an empty list, so a
    /// torrent that came back this way had nobody to announce to — for as long
    /// as it lived.
    /// </para>
    /// <para>
    /// Watched on the owner's server, 3 September 2026. Dark Matter S02E02
    /// announced once to fifty-nine trackers when it was first grabbed and
    /// never again: <c>trackers=0</c> in its resume file, no swarm size on the
    /// page, and one announce line in the journal where the torrent beside it —
    /// whose magnet happened to carry twenty-one — had them every interval.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrabTheClientHasLostComesBackWithItsTrackersAndNotWithNone()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        StandingEngine engine = new();

        await Transfers(engine, grabs, Server())
            .TickAsync(Incomplete, Intake, CancellationToken.None, ["udp://tracker.example.invalid:6969/announce"]);

        TorrentRequest again = Assert.Single(engine.Taken);

        Assert.Contains(
            "udp://tracker.example.invalid:6969/announce",
            again.Trackers,
            StringComparer.OrdinalIgnoreCase);
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

        // The server refuses this one, for a reason that can change: an encoder
        // busy, a preset missing, a library it will not write to yet.
        server.Encoder.Refusal = "the encoder is not ready for that library";

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(staged), "It was never staged.");
        Assert.Null(server.Encoder.Job);

        StoredDownload waiting = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Staged, waiting.State);
        Assert.Equal([staged], waiting.StagedPaths);

        // The download is gone from the incomplete folder, so a tick that tried
        // to stage again would have nothing to copy and would say so.
        Assert.False(File.Exists(episode));

        // Now it will take it, and the next tick asks again.
        server.Encoder.Refusal = null;

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.NotNull(server.Encoder.Job);
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
    /// <strong>A file this plugin did not stage is not this plugin's</strong>, whatever it is called. It used
    /// to be matched to a grab by the release in its name and taken on — dispatched, and deleted once the
    /// library had it — so a file the owner, or anybody else, put in the intake folder under a release's name
    /// could be encoded and then deleted by a plugin that never made it.
    /// </para>
    /// <para>
    /// The owner's rule of 16 September 2026: the plugin deletes and takes on only what it created itself.
    /// Here the name is the grab's release to the letter, and the file is still neither dispatched nor
    /// touched.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFileThisPluginDidNotStageIsNeitherDispatchedNorDeleted()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        Directory.CreateDirectory(Intake);

        string somebodys = Path.Combine(Intake, "Silo.S03E06.1080p.WEB.H264-CAKES.mkv");

        await File.WriteAllBytesAsync(somebodys, new byte[2048]);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        FakeProvider server = Server();

        await Transfers(new StandingEngine(), grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Null(server.Encoder.Job);
        Assert.True(File.Exists(somebodys), "A file this plugin did not stage was deleted.");
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// What the plugin did stage is its own to clear once nothing waits on it: the staged path is written
    /// against the grab, which is the proof. A grab that is over — failed here — leaves its staged file, and
    /// the next pass takes it away.
    /// </remarks>
    [Fact]
    public async Task AFileThisPluginStagedForAGrabThatIsOverIsCleared()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        Directory.CreateDirectory(Intake);

        string ours = Path.Combine(Intake, "Silo.2023.S03E06.1080p.mkv");

        await File.WriteAllBytesAsync(ours, new byte[2048]);
        await grabs.StagedAsync(Hash, [ours], CancellationToken.None);
        await grabs.StateAsync(Hash, GrabState.Failed, CancellationToken.None);

        await Transfers(new StandingEngine(), grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.False(File.Exists(ours), "A file this plugin staged for a grab that is over was left.");
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
        await grabs.StagedAsync(Hash, [Staged], CancellationToken.None);

        await Transfers(new StandingEngine(), grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c> § Switching a show on, the owner's answer of 15 September 2026: a
    /// download of a show that has nothing searched is cancelled and what it downloaded is deleted.
    /// Which shows those are, through the plugin's own repositories, is <c>TheSwitchDecidesTests</c>.
    /// </remarks>
    [Fact]
    public async Task AGrabForAShowThatIsNotSearchedIsCancelledAndDeleted()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        StandingEngine engine = new StandingEngine().Holding(Downloading());

        await Transfers(engine, grabs, Server(), searched: false)
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
    /// A grab an earlier version started in a folder of its own finishes there, so it is staged from there and
    /// not from the folder every other torrent uses — or the finished file is never found and the episode never
    /// reaches the library. <c>grabs.folder</c> stays read for as long as such a grab can be open.
    /// </remarks>
    [Fact]
    public async Task AGrabWithAFolderOfItsOwnIsStagedFromIt()
    {
        GrabRepository grabs = await Grabs();
        await Racing(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000, RivalFolder);

        StandingEngine engine = new StandingEngine()
            .Holding(Finished() with { InfoHash = Rival }, new TorrentFile(Path.GetFileName(episode), 900_000_000));

        await Transfers(engine, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "A download in a folder of its own was never staged.");
    }

    /// <remarks>
    /// Until 15 September 2026 a second torrent of one release was started beside the first, in a folder of
    /// its own. Nothing starts one any more, but a grab an earlier version wrote that way can still be open:
    /// after a restart it goes back into the same folder, where its data is. Put into the one every torrent
    /// uses, it would start again from nothing.
    /// </remarks>
    [Fact]
    public async Task AGrabWithAFolderOfItsOwnIsAddedBackIntoThatFolder()
    {
        GrabRepository grabs = await Grabs();
        await Racing(grabs);

        StandingEngine engine = new();

        await Transfers(engine, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(RivalFolder, Assert.Single(engine.Taken).DownloadFolder);
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


        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Encoder.Dispatches);
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode the server says failed is said, and waited on.</strong>
    /// <c>VideoEncodeJob</c> publishes <c>EncodingFailedEvent</c> for every
    /// exception it meets, a server stop included, and the queue then tries the
    /// job again — up to three attempts, and a stop does not count as one. So a
    /// failure is not the end of the job. Treating it as the end put the episode
    /// back to missing, failed the grab, and let the sweep take the staged file,
    /// after which the server's next attempt failed with "input file not found".
    /// </para>
    /// <para>
    /// The owner's ruling of 14 September 2026: nothing is closed on it. The
    /// reason goes to the History page once, in the server's own words, and the
    /// grab waits until the library has the episode or the owner cancels it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeTheServerSaysFailedIsWaitedOnAndItsReasonSaidOnce()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();


        // A server that names the job it queued, and then says it died.
        RecordingEncoder encoder = new() { JobId = "01KZGKX2G0966V80H26EKGG5T1" };
        SaidOfEverything jobs = new(new(EncodeJobState.Failed, "the source file has no audio stream"));

        Transfers transfers = new(
            engine,
            grabs,
            new HostLibrary(new FakeLibraryQuery()
                .Library(TelevisionLibrary, "Television", "tv")
                .Show(41, "Silo", TelevisionLibrary, year: 2023)
                .Episode(41, 3, 6)
                .Episode(41, 1, 1, hasFile: true)),
            AppliedToEveryShow.Searched,
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

        // The server says the attempt failed, and two passes follow.
        clock.Advance(TimeSpan.FromMinutes(1));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Still waiting, still covering its episode, its staged file still
        // there for the server's next attempt, and nothing asked for twice.
        StoredDownload waiting = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(GrabState.Dispatched, waiting.State);
        Assert.Equal([Episode], waiting.Covers);
        Assert.True(File.Exists(Staged), "the staged file the server will try again was taken away");
        Assert.Single(encoder.Asked);

        // In the server's own words, once, on the History page and on the row.
        Assert.Single(
            await grabs.HistoryAsync(CancellationToken.None),
            one => (one.Detail ?? string.Empty).Contains("no audio stream", StringComparison.Ordinal));
        Assert.Contains("no audio stream", transfers.FailureOf(Hash) ?? string.Empty, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// A pack for a show that is in no library gets the show added, which is the
    /// one thing that turns it into an ordinary grab: once it is in a library it
    /// has episodes, and an episode has the id an encode is asked for by.
    /// </para>
    /// <para>
    /// It is the same call the dashboard's <em>Add content</em> makes, into the
    /// library the files read as — numbered by season is television. Nothing
    /// else happens on the tick that asks: the import runs on the server's own
    /// queue, so the files stay where they are and the grab stays open until the
    /// show lands.
    /// </para>
    /// <para>
    /// <strong>And nothing is handed to the encoder unnamed.</strong> The
    /// server's <c>PluginEncoder</c> writes the <c>mediaId</c> straight into
    /// <c>VideoEncodeJob.Id</c>, and that job resolves it against
    /// <c>Movies.Id</c> or <c>Episodes.Id</c> and nothing else; with no id it
    /// resolves no row, returns having done no work, and the queue records the
    /// job as finished. On 31 August 2026 that was nine files handed over, nine
    /// jobs reported finished inside two minutes, and an empty library.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APackForAShowInNoLibraryHasItsShowAdded()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string first = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);
        string second = Downloaded("Dark.Matter.2024.S01E02.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(first), 900_000_000),
            new TorrentFile(Path.GetFileName(second), 900_000_000));

        FakeProvider server = Server();
        RecordingEncoder encoder = new();
        RecordingImport imports = new();

        await Transfers(engine, grabs, server, encoder: encoder, imports: imports)
            .TickAsync(Incomplete, Intake, CancellationToken.None);

        // The show, once, by the title and year its own files carry, into the
        // television library — the files are numbered by season.
        (string title, int? year, Library into) = Assert.Single(imports.Added);

        Assert.Equal("Dark Matter", title);
        Assert.Equal(2024, year);
        Assert.Equal(LibraryKind.Television, into.Kind);

        // No encode yet: there is no episode row to name one by until the import
        // has run, and asking without one registers against nothing.
        Assert.Empty(encoder.Asked);

        // Both files still where the download left them, and nothing staged, so
        // the sweep of the intake folder has nothing of this pack to delete.
        Assert.True(File.Exists(first), "the first file was not left where it was");
        Assert.True(File.Exists(second), "the second file was not left where it was");
        Assert.False(Directory.Exists(Intake) && Directory.EnumerateFiles(Intake).Any());

        // Left open, so the tick after the import lands takes it on.
        Assert.Single(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// And it is asked for once, however long the import takes. The import runs
    /// on the server's own queue, so a tick a minute later still finds the show
    /// in no library — and without this that dispatched the same import again,
    /// and again, for as long as the queue took.
    /// </remarks>
    [Fact]
    public async Task TheShowIsAskedForOnceHoweverManyTicks()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string episode = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();
        RecordingImport imports = new();

        Transfers transfers = Transfers(engine, grabs, server, encoder: new RecordingEncoder(), imports: imports);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Single(imports.Added);
    }

    /// <remarks>
    /// A show no provider knows is asked for again on the next tick rather than
    /// remembered as done — nothing was dispatched, so there is nothing to wait
    /// for — and the History page says which show its files name, because that
    /// is then the only thing the owner can act on.
    /// </remarks>
    [Fact]
    public async Task AShowNoProviderKnowsIsSaidOnTheHistoryPage()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string episode = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();
        RecordingImport imports = new() { Answers = null };

        Transfers transfers = Transfers(engine, grabs, server, encoder: new RecordingEncoder(), imports: imports);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Asked again, because nothing was dispatched and the answer can change.
        Assert.Equal(2, imports.Added.Count);

        Assert.True(File.Exists(episode), "the download was not left where it was");

        // Said once, on the History page, naming the show the files claim to be.
        IReadOnlyList<HistoryRow> history = await grabs.HistoryAsync(CancellationToken.None);

        Assert.Single(history, one => (one.Detail ?? string.Empty).Contains("Dark Matter", StringComparison.Ordinal)
                                      && (one.Detail ?? string.Empty).Contains("no library", StringComparison.Ordinal));
    }

    /// <remarks>
    /// On a server that offers none of the parts that add a show, the pack is
    /// left where it is and the History page says which show it holds — said
    /// once, however long the torrent sits there. This runs every minute, and a
    /// line a minute buries the page it is written on.
    /// </remarks>
    [Fact]
    public async Task OnAServerThatCannotAddAShowThePackIsSaidOnceAndLeftAlone()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string episode = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();
        RecordingEncoder encoder = new();

        Transfers transfers = Transfers(engine, grabs, server, encoder: encoder);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(encoder.Asked);
        Assert.True(File.Exists(episode), "the download was not left where it was");

        // Said once, on the History page, naming the show the files claim to be
        // so the owner knows which one to add.
        IReadOnlyList<HistoryRow> history = await grabs.HistoryAsync(CancellationToken.None);

        Assert.Single(history, one => (one.Detail ?? string.Empty).Contains("Dark Matter", StringComparison.Ordinal)
                                      && (one.Detail ?? string.Empty).Contains("no library", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The same for the ordinary path, which had the same fault and nobody had
    /// read the line: an episode dispatched to a library says which library by
    /// the name the owner gave it.
    /// </remarks>
    [Fact]
    public async Task ADispatchedEpisodeSaysWhichLibraryByTheNameTheOwnerGaveIt()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        await Transfers(engine, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        HistoryRow line = Assert.Single(
            await grabs.HistoryAsync(CancellationToken.None),
            one => one.Event == "dispatched");

        Assert.Equal("Silo", line.ShowTitle);

        Assert.Contains("Television", line.Detail ?? string.Empty, StringComparison.Ordinal);
        Assert.DoesNotContain(TelevisionLibrary, line.Detail ?? string.Empty, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// A handed-over pack covers no episode — that is precisely why it was
    /// handed over — and the rule that finishes a grab reads "every episode it
    /// covers has a file". Over no episodes that holds vacuously, so the tick
    /// after the handover called the encode landed and deleted the torrent and
    /// everything it had downloaded.
    /// </para>
    /// <para>
    /// On 31 August 2026 that was 36 GB of Dark Matter, two minutes after it was
    /// handed over — and while the server was still reading those very files,
    /// because a handover gives the encoder their paths in the download folder
    /// rather than a copy.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AHandedOverPackIsNotDeletedWhileItsEncodesAreStillRunning()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string first = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);
        string second = Downloaded("Dark.Matter.2024.S01E02.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(first), 900_000_000),
            new TorrentFile(Path.GetFileName(second), 900_000_000));

        FakeProvider server = Server();

        Transfers transfers = Transfers(
            engine,
            grabs,
            server,
            encoder: new RecordingEncoder { JobId = "01KZGKX2G0966V80H26EKGG5T1" },
            says: new SaidOfEverything(new(EncodeJobState.Running, null)));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Taking the torrent out of the client is fine and leaves the bytes
        // alone; taking it out *with its files* is the deletion, and that is
        // the call that took the 36 GB.
        Assert.DoesNotContain(engine.Removed, one => one.DeleteFiles);
    }

    /// <remarks>
    /// And a server that cannot say deletes nothing at all. There is no episode
    /// to watch for and no job to ask about, so there is nothing that could ever
    /// say the encode finished — and "it has been a while" is not that.
    /// </remarks>
    [Fact]
    public async Task AHandedOverPackIsNeverDeletedByAServerThatCannotSayWhetherItsEncodesFinished()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string only = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(only), 900_000_000));

        FakeProvider server = Server();

        // No IPluginJobs, which is every server before 0.1.479.
        Transfers transfers = Transfers(engine, grabs, server, encoder: new RecordingEncoder());

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.DoesNotContain(engine.Removed, one => one.DeleteFiles);
    }

    /// <remarks>
    /// <para>
    /// And not even when every job says it finished. On 31 August 2026 nine of
    /// them came back finished inside two minutes, the library gained not one
    /// file, and the pack was deleted on their word — the same 36 GB, the
    /// second time in one evening.
    /// </para>
    /// <para>
    /// A server saying a job is over is not the episode being there, and for a
    /// pack handed over to be identified there is nothing else to ask: the
    /// plugin does not know which episodes those files became, so it can never
    /// see them arrive. What it cannot verify it does not throw away.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AHandedOverPackIsNotDeletedEvenWhenEveryJobSaysItFinished()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string first = Downloaded("Dark.Matter.2024.S01E01.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);
        string second = Downloaded("Dark.Matter.2024.S01E02.1080p.ATVP.WEB-DL.H.264-FLUX.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(first), 900_000_000),
            new TorrentFile(Path.GetFileName(second), 900_000_000));

        FakeProvider server = Server();

        Transfers transfers = Transfers(
            engine,
            grabs,
            server,
            encoder: new RecordingEncoder { JobId = "01KZGKX2G0966V80H26EKGG5T1" },
            says: new SaidOfEverything(new(EncodeJobState.Finished, null)));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.DoesNotContain(engine.Removed, one => one.DeleteFiles);
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode the server finished and then filed against the wrong
    /// episode is done.</strong> On 1 September 2026 the plugin dispatched South
    /// Park S15E12 with the server's own id for it, <c>153823</c>; the encoder
    /// logged <c>for 153823</c> and wrote
    /// <c>South.Park.S15E12.1%.NoMercy.m3u8</c> into the show's own folder, and
    /// the post-encode registration attached that file to episode
    /// <c>153785</c> — season 0, "Chef Aid: Behind The Menu", twice over. The
    /// real S15E12 was left with no file at all.
    /// </para>
    /// <para>
    /// So the library said the episode was still missing, the queue was empty,
    /// and the Downloads page said "encoding" for six hours before the plugin
    /// gave up and downloaded the same episode again. The file was on disk under
    /// the right name the whole time.
    /// </para>
    /// <para>
    /// Only ever on a job the server has said is finished. Read while one is
    /// running, a file being written would be taken for a file that arrived and
    /// the download deleted underneath the encoder — the fault that cost 36 GB.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeTheServerFiledUnderAnotherEpisodeIsStillDone()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        // Not seeding: a torrent still giving something back is left alone
        // whatever the library says, and that guard would mask this one.
        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        // The library never gains the episode — encoded: false — and the show's
        // files carry one named for it, which is where the encoder really put
        // it.
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 3, 6, hasFile: false)
            .Episode(41, 1, 1, hasFile: true)

            // Filed against season 1 episode 1, as the server's registration
            // really does it, and named for the episode it really is.
            .File(41, 1, 1, "/Silo.(2023)/Silo.S03E06/Silo.S03E06.The.Dive.NoMercy.m3u8");

        Transfers transfers = new(
            engine,
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            new RecordingEncoder { JobId = "01KZGKX2G0966V80H26EKGG5T1" },
            server.Journal,
            server.Log,
            TimeProvider.System,
            new SaidOfEverything(new(EncodeJobState.Finished, null)));

        // Staged and dispatched on the first, finished on the second.
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Done, not waited out: the torrent is cleared up with its files and the
        // grab is closed, so the episode is never looked for a second time.
        Assert.Contains(engine.Removed, one => one.DeleteFiles);
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // And said out loud, because the owner's dashboard shows the episode
        // under a season it does not belong to and nothing else explains it.
        Assert.Contains(
            server.Journal.Snapshot().History,
            one => (one.Detail ?? string.Empty).Contains("under another episode", StringComparison.Ordinal));
    }

    /// <remarks>
    /// And a finished job that wrote nothing at all is not done. Nine of them
    /// came back finished inside two minutes on 31 August 2026 and the library
    /// gained not one file; taking a server's word for it there deleted 36 GB.
    /// </remarks>
    [Fact]
    public async Task AFinishedJobThatWroteNothingIsNotDone()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        // Not seeding, so nothing but this rule can decide the outcome.
        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        Transfers transfers = Transfers(
            engine,
            grabs,
            server,
            encoder: new RecordingEncoder { JobId = "01KZGKX2G0966V80H26EKGG5T1" },
            says: new SaidOfEverything(new(EncodeJobState.Finished, null)));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.DoesNotContain(engine.Removed, one => one.DeleteFiles);
        Assert.NotEmpty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>Every file a pack stages is one something is waiting on.</strong>
    /// Staging wrote nine episodes into the intake folder, recorded one of them
    /// and answered with that one, so the tick's list of files something is
    /// waiting on held one — and the sweep that clears the folder deleted the
    /// other eight, one second after their encodes had been asked for. Every
    /// one of those encodes then failed for want of the file it was pointed at.
    /// </para>
    /// <para>
    /// It happened on 1 September 2026 to a nine-episode Dark Matter pack: nine
    /// dispatched at 12:22:40, eight cleared between 12:22:41 and 12:22:46, and
    /// one episode in the library at the end of it. A grab of one episode never
    /// showed it, because the one path recorded was the only path there was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryFileAPackStagesSurvivesTheSweepThatFollowsIt()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        string first = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);
        string second = Downloaded("Silo.S03E07.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(first), 900_000_000),
            new TorrentFile(Path.GetFileName(second), 900_000_000));

        FakeProvider server = Server();
        RecordingEncoder encoder = new();

        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 3, 6, hasFile: false)
            .Episode(41, 3, 7, hasFile: false)
            .Episode(41, 1, 1, hasFile: true);

        Transfers transfers = new(
            engine,
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            encoder,
            server.Journal,
            server.Log,
            TimeProvider.System);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        // Both episodes asked for, each pointed at its own file.
        Assert.Equal(2, encoder.Asked.Count);
        Assert.Equal(2, encoder.Asked.Select(one => one.StagedFile).Distinct(StringComparer.Ordinal).Count());

        // And both files still in the intake folder when the tick is over. The
        // sweep runs inside the same tick as the staging, so this is the whole
        // of the fault: an encode was asked for and its file taken away.
        Assert.All(
            encoder.Asked,
            one => Assert.True(File.Exists(one.StagedFile), $"{Path.GetFileName(one.StagedFile)} was swept away after its encode was asked for."));
    }

    /// <remarks>
    /// And a pack asked for again after a restart points each episode at its
    /// own file. The store kept one path for a whole pack, so every episode was
    /// re-dispatched against the same video — nine encodes of one episode, each
    /// registered against a different row.
    /// </remarks>
    [Fact]
    public async Task APackAskedForAgainPointsEachEpisodeAtItsOwnFile()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        Directory.CreateDirectory(Intake);

        string first = Path.Combine(Intake, "Silo.2023.S03E06.1080p.mkv");
        string second = Path.Combine(Intake, "Silo.2023.S03E07.1080p.mkv");

        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");

        await grabs.CoversAsync(Hash, [new(41, 3, 6), new(41, 3, 7)], CancellationToken.None);
        await grabs.StagedAsync(Hash, [first, second], CancellationToken.None);

        FakeProvider server = Server();
        RecordingEncoder encoder = new();

        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 3, 6, hasFile: false)
            .Episode(41, 3, 7, hasFile: false)
            .Episode(41, 1, 1, hasFile: true);

        Transfers transfers = new(
            new StandingEngine(),
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            encoder,
            server.Journal,
            server.Log,
            TimeProvider.System);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(2, encoder.Asked.Count);

        Assert.Contains(encoder.Asked, one => one.Episode.Number == 6 && one.StagedFile == first);
        Assert.Contains(encoder.Asked, one => one.Episode.Number == 7 && one.StagedFile == second);
    }

    /// <remarks>
    /// <strong>One episode's encode failing costs a pack nothing.</strong> It used
    /// to fail the whole grab — on 1 September 2026 episode one's encode died,
    /// the grab went with it, and the sweep took the staged files of all nine —
    /// and then it took the failed episode off the grab and put it back to
    /// missing. Neither is right while the server's queue is about to try that
    /// encode again: the pack keeps every episode and every file.
    /// </remarks>
    [Fact]
    public async Task AFailedEncodeInAPackTakesNothingOffIt()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        Directory.CreateDirectory(Intake);

        string first = Path.Combine(Intake, "Silo.2023.S03E06.1080p.mkv");
        string second = Path.Combine(Intake, "Silo.2023.S03E07.1080p.mkv");

        await File.WriteAllTextAsync(first, "one");
        await File.WriteAllTextAsync(second, "two");

        await grabs.CoversAsync(Hash, [new(41, 3, 6), new(41, 3, 7)], CancellationToken.None);
        await grabs.StagedAsync(Hash, [first, second], CancellationToken.None);
        await grabs.StateAsync(Hash, GrabState.Dispatched, CancellationToken.None);

        FakeProvider server = Server();

        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 3, 6, hasFile: false)
            .Episode(41, 3, 7, hasFile: false)
            .Episode(41, 1, 1, hasFile: true);

        Transfers transfers = new(
            new StandingEngine().Holding(Finished() with { State = TorrentState.Finished }),
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            new RecordingEncoder(),
            server.Journal,
            server.Log,
            TimeProvider.System,
            new SaidPerEpisode(new()
            {
                // S03E06 and S03E07, as the server numbers them.
                [41306] = new(EncodeJobState.Failed, "the source has no audio stream"),
                [41307] = new(EncodeJobState.Running, null),
            }));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        StoredDownload after = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        // Both episodes still on the grab, and the grab still waiting.
        Assert.Equal([new EpisodeKey(41, 3, 6), new EpisodeKey(41, 3, 7)], after.Covers);
        Assert.Equal(GrabState.Dispatched, after.State);

        // And neither file was taken away, because one of them is still being
        // read and the other has only just stopped being.
        Assert.True(File.Exists(first), "the failed episode's file was swept");
        Assert.True(File.Exists(second), "the file of an encode still running was swept");
    }

    /// <remarks>
    /// <strong>A file an encode is still reading is never swept.</strong> The
    /// sweep decides on "is a grab waiting on this", and a grab that has just
    /// failed is waiting on nothing — so it deleted the input of an encode that
    /// was between its first bundle and its second, and that encode then failed
    /// with "input file not found". The server saying a job is still going is
    /// the only thing that knows.
    /// </remarks>
    [Fact]
    public async Task AFileAnEncodeIsStillReadingIsNeverSwept()
    {
        GrabRepository grabs = await Grabs();
        await ByHand(grabs);

        Directory.CreateDirectory(Intake);

        string encoding = Path.Combine(Intake, "Silo.2023.S03E06.1080p.mkv");

        await File.WriteAllTextAsync(encoding, "still being read");

        await grabs.CoversAsync(Hash, [new(41, 3, 6)], CancellationToken.None);
        await grabs.StagedAsync(Hash, [encoding], CancellationToken.None);

        // Finished with, as far as the grab goes — which is exactly the state
        // that used to make the sweep take the file.
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        FakeProvider server = Server();

        Transfers transfers = Transfers(
            new StandingEngine(),
            grabs,
            server,
            says: new SaidOfEverything(new(EncodeJobState.Running, null)));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(encoding), "the input of a running encode was swept away under it");
    }

    /// <summary>A server that answers about each job by name.</summary>
    /// <summary>A server that has said a different thing about each media row.</summary>
    /// <remarks>
    /// Keyed by the media id, because that is what the server's own encoding
    /// events carry — the row the encode registers against, which is the
    /// episode id this plugin named when it asked. <c>FakeLibraryQuery</c>
    /// numbers an episode <c>show * 1000 + season * 100 + number</c>, so
    /// Silo S03E06 is 41306.
    /// </remarks>
    private sealed class SaidPerEpisode(Dictionary<int, EncodeJob> said) : IEncoderSays
    {
        public EncodeJob? About(int mediaId)
        {
            return said.GetValueOrDefault(mediaId);
        }
    }

    /// <summary>A server that has said the same thing about every encode.</summary>
    private sealed class SaidOfEverything(EncodeJob said) : IEncoderSays
    {
        public EncodeJob? About(int mediaId)
        {
            return said;
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>No clock decides an encode is lost; the library decides it has
    /// arrived.</strong> The encoder says when it finished and when it failed. It
    /// says nothing about a job the owner took out of the queue by hand, or one
    /// that ended with nothing to encode — and for those this gave up after six
    /// hours, marked the grab failed, and so put the episode back to missing to
    /// be downloaded a second time.
    /// </para>
    /// <para>
    /// The owner's ruling of 14 September 2026: the library decides. Seven hours
    /// on with nothing heard, the grab is still waiting and nothing has been
    /// asked twice; the pass after the library has the episode closes it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeNobodyHearsAboutIsWaitedOnUntilTheLibraryHasIt()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 24, 12, 0, 0, TimeSpan.Zero));

        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        Transfers transfers = Transfers(engine, grabs, server, clock: clock);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        // Seven hours later nothing has been heard and the library does not
        // have it yet.
        clock.Advance(TimeSpan.FromHours(7));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);
        Assert.Equal(1, server.Encoder.Dispatches);

        // And then it does.
        Transfers arrived = Transfers(engine, grabs, server, encoded: true, clock: clock);

        await arrived.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
        Assert.Equal(1, server.Encoder.Dispatches);
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


        Transfers transfers = Transfers(engine, grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Encoder.Dispatches);

        // The same release under the site's name for it, as a second staging
        // left behind. It is a copy of what was already dispatched.
        File.Copy(
            Staged,
            Path.Combine(Intake, "Silo.S03E06.1080p.WEB.H264-CAKES EZTV.mkv"));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(1, server.Encoder.Dispatches);
    }

    /// <remarks>
    /// <para>
    /// <strong>Nothing in the intake folder that this plugin did not stage is touched</strong> — the owner's
    /// rule of 16 September 2026. The folder is shared: on that day the sweep that cleared "whatever no grab
    /// waits on, folders included" emptied a folder of somebody else's that had nothing to do with the plugin.
    /// </para>
    /// <para>
    /// A folder, a loose file from nobody's grab and a second copy of a staged episode made by somebody else
    /// all stay exactly where they are, and so does what the plugin staged and is waiting on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NothingInTheIntakeFolderThisPluginDidNotStageIsTouched()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished(),
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();


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

        string[] before = [.. Directory.EnumerateFileSystemEntries(Intake, "*", SearchOption.AllDirectories).Order()];

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "What is waited on was cleared.");
        Assert.Equal(before, Directory.EnumerateFileSystemEntries(Intake, "*", SearchOption.AllDirectories).Order());
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode dispatched before a restart is not asked for
    /// again.</strong> The server's queue outlives a restart, and the job it
    /// holds says what became of it when it runs. Asking a second time put a
    /// second job in that queue for every grab that was waiting — on
    /// 14 September 2026 the owner restarted the server and found the encoder
    /// busy with episodes it had already been asked for.
    /// </para>
    /// <para>
    /// A fresh instance, as a restart gives, and two passes over the grab the
    /// run before left dispatched: nothing is sent, and it is still waiting.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeDispatchedBeforeARestartIsNotAskedForAgain()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        Directory.CreateDirectory(Intake);
        await File.WriteAllBytesAsync(Staged, new byte[2048]);

        // Dispatched by the run before this one, which is all the store keeps.
        await grabs.StagedAsync(Hash, [Staged], CancellationToken.None);
        await grabs.StateAsync(Hash, GrabState.Dispatched, CancellationToken.None);

        FakeProvider server = Server();


        // A fresh instance, as a restart gives.
        Transfers transfers = Transfers(new StandingEngine(), grabs, server);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(0, server.Encoder.Dispatches);
        Assert.Equal(
            GrabState.Dispatched,
            Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);
    }

    /// <remarks>
    /// <para>
    /// <strong>A finished download is moved out of the download folder, and nothing of it stays
    /// behind.</strong> On 16 September 2026 South Park S15E12 CtrlHD finished at 16:14. The plugin copied its
    /// 1.3 GB byte by byte into the intake folder, where the episode appeared at 16:49, and could not take the
    /// download away afterwards, because the client still held the file open. For thirty-five minutes the
    /// owner saw nothing moved at all, and after that the whole release was still in the download folder.
    /// </para>
    /// <para>
    /// A torrent the client has finished with owes nothing more, so it is let go of first and its video is
    /// moved; the rest of what it downloaded goes with the folder it came in. The client here holds the files
    /// exactly as the real one does, and lets go of them only when the torrent is removed.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadIsMovedOutOfTheDownloadFolderWhole()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string release = Path.Combine(Incomplete, "Silo.S03E06.1080p.WEB.H264-CAKES");
        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000, release);
        string notes = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.nfo", 4_120, release);

        using HoldingClient client = new(
            new StandingEngine().Holding(
                Finished() with { State = TorrentState.Finished },
                new TorrentFile("Silo.S03E06.1080p.WEB.H264-CAKES/Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000),
                new TorrentFile("Silo.S03E06.1080p.WEB.H264-CAKES/Silo.S03E06.1080p.WEB.H264-CAKES.nfo", 4_120)),
            episode,
            notes);

        FakeProvider server = Server();

        await Transfers(client, grabs, server).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.False(File.Exists(episode), "The download was left where it was.");
        Assert.False(Directory.Exists(release), "The rest of the release was left in the download folder.");
        Assert.NotNull(server.Encoder.Job);
    }

    /// <remarks>
    /// The other half. A torrent still seeding owes the swarm what its seeding rules say, and it seeds out of
    /// the file it downloaded: that file is copied into the intake folder and left with the client, and the
    /// torrent is not let go of.
    /// </remarks>
    [Fact]
    public async Task ADownloadStillSeedingIsCopiedAndLeftWithTheClient()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string release = Path.Combine(Incomplete, "Silo.S03E06.1080p.WEB.H264-CAKES");
        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000, release);

        using HoldingClient client = new(
            new StandingEngine().Holding(
                Finished(),
                new TorrentFile("Silo.S03E06.1080p.WEB.H264-CAKES/Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000)),
            episode);

        await Transfers(client, grabs, Server()).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.True(File.Exists(Staged), "It was never staged.");
        Assert.True(File.Exists(episode), "A download the client is still seeding from was taken away.");
        Assert.Empty(client.Removed);
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode the server skips says nothing, and the library still decides.</strong> Asked to
    /// encode a file whose outputs are already there, the server's <c>VideoEncodeJob</c> logs "skipping
    /// preset … all desired outputs present" and returns — no started, no completed, no failed. On
    /// 16 September 2026 that was South Park S15E12, whose file the library holds under its own name but
    /// registered against season 0 (media-server #38). The grab waited on an encode nobody would ever speak
    /// of, and the download stayed where it was.
    /// </para>
    /// <para>
    /// Nothing heard about it, and a file in the show's folders named for the episode: it is done. A job the
    /// server says is still running is still waited on, which is the rule that kept 36 GB.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeTheServerSkippedWithoutAWordIsDoneOnceTheLibraryHasItUnderItsName()
    {
        GrabRepository grabs = await Grabs();
        await Grabbed(grabs);

        string episode = Downloaded("Silo.S03E06.1080p.WEB.H264-CAKES.mkv", 900_000_000);

        StandingEngine engine = new StandingEngine().Holding(
            Finished() with { State = TorrentState.Finished },
            new TorrentFile(Path.GetFileName(episode), 900_000_000));

        FakeProvider server = Server();

        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)
            .Episode(41, 3, 6, hasFile: false)
            .Episode(41, 1, 1, hasFile: true)
            .File(41, 1, 1, "/Silo.(2023)/Silo.S03E06/Silo.S03E06.The.Dive.NoMercy.m3u8");

        Transfers transfers = new(
            engine,
            grabs,
            new HostLibrary(query),
            AppliedToEveryShow.Searched,
            new Stager(server.Journal, server.Log),
            new RecordingEncoder { JobId = "01KZGKX2G0966V80H26EKGG5T1" },
            server.Journal,
            server.Log,
            TimeProvider.System,
            new SaidPerEpisode([]));

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
        Assert.False(File.Exists(Staged), "The staged copy was left in the intake folder.");
    }

    /// <summary>A client that keeps a torrent's files open the way the real one does, until it is removed.</summary>
    private sealed class HoldingClient : ITorrentEngine, IDisposable
    {
        private readonly StandingEngine _engine;
        private readonly List<FileStream> _open;

        public HoldingClient(StandingEngine engine, params string[] files)
        {
            _engine = engine;

            // Exactly how TorrentDisk holds them: read and write, shared both ways and no further.
            _open = [.. files.Select(path => new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))];
        }

        public List<(string InfoHash, bool DeleteFiles)> Removed => _engine.Removed;

        public Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct) => _engine.AddAsync(request, ct);

        public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct) => _engine.StatusAsync(ct);

        public Task PauseAsync(string infoHash, CancellationToken ct) => _engine.PauseAsync(infoHash, ct);

        public Task ResumeAsync(string infoHash, CancellationToken ct) => _engine.ResumeAsync(infoHash, ct);

        public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct) => _engine.FilesAsync(infoHash, ct);

        public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
        {
            LetGo();

            return _engine.RemoveAsync(infoHash, deleteFiles, ct);
        }

        public void Dispose() => LetGo();

        private void LetGo()
        {
            foreach (FileStream one in _open)
            {
                one.Dispose();
            }

            _open.Clear();
        }
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    /// <summary>A torrent an earlier version started beside another of the same release.</summary>
    private const string Rival = "FEDCBA9876543210FEDCBA9876543210FEDCBA98";

    private static EpisodeKey Episode => new(41, 3, 6);

    private string Incomplete => Path.Combine(_root, "incomplete");

    /// <summary>Where that torrent downloads, which is a folder of its own.</summary>
    private string RivalFolder => Path.Combine(Incomplete, Rival);

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

    /// <summary>The second torrent of the same release, as an earlier version recorded it.</summary>
    private async Task Racing(GrabRepository grabs)
    {
        await grabs.RecordAsync(
            Episode,
            "Silo",
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            "The Pirate Bay",
            Rival,
            $"magnet:?xt=urn:btih:{Rival}",
            [Episode],
            DateTimeOffset.UtcNow,
            CancellationToken.None,
            RivalFolder);
    }

    /// <summary>A file really on disk, where the download would have left it.</summary>
    private string Downloaded(string name, long length, string? folder = null)
    {
        Directory.CreateDirectory(folder ?? Incomplete);

        string path = Path.Combine(folder ?? Incomplete, name);

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
        ITorrentEngine engine,
        GrabRepository grabs,
        FakeProvider server,
        bool encoded = false,
        bool searched = true,
        TimeProvider? clock = null,
        IEncoderSays? says = null,
        IEncodeGateway? encoder = null,
        IShowImport? imports = null)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            // A real Ulid, because the server's library id is one and the
            // encode job will not take anything else. "library-tv" made every
            // test here agree with a plugin that could never dispatch.
            .Library(TelevisionLibrary, "Television", "tv")
            .Show(41, "Silo", TelevisionLibrary, year: 2023)

            // Whether the encode has landed. It is the only thing the plugin
            // can see that says the job finished.
            .Episode(41, 3, 6, hasFile: encoded)
            .Episode(41, 1, 1, hasFile: true);

        return new(
            engine,
            grabs,
            new HostLibrary(query),
            searched ? AppliedToEveryShow.Searched : AppliedToEveryShow.NotSearched,
            new Stager(server.Journal, server.Log),
            encoder ?? EncodeGateway.For(server, server.Journal, server.Log),
            server.Journal,
            server.Log,
            clock ?? TimeProvider.System,
            says,
            imports);
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
