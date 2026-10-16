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
/// A wrong episode the plugin delivered is replaced on its own, and the old one goes to the server's recycle
/// area.
/// </summary>
/// <remarks>
/// <para>
/// The owner's rule of 15 October 2026 (<c>docs/specs/run.md</c> § When a download finishes, § What the plugin
/// deletes). South Park S29E03 was delivered at 03:07 that day from ViETNAM's HDTV capture, grab 99 on the
/// owner's server; CAKES's WEB release reached the name sources at 06:00.
/// </para>
/// <para>
/// The library is a real folder laid out as the server's <c>VideoEncodeJob</c> lays an episode out —
/// <c>{library folder}/South.Park.(1997)/South.Park.S29E03/</c> with the master playlist the library registered
/// and the video beside it — and the library answers that file the way the owner's server does, naming the show's
/// and the episode's folders twice. The server's library writer is a double that records and can refuse: it is
/// the server's facade. Everything on the plugin's side runs for real.
/// </para>
/// </remarks>
public sealed class AWrongEpisodeIsReplacedTests : IDisposable
{
    private const string TelevisionLibrary = "01HQ5W4AVF30N10RT6XCF6AJHM";

    private const string Vietnam = "South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM";

    private const string Cakes = "South.Park.S29E03.1080p.WEB.H264-CAKES";

    private const string OldHash = "7445E4910B7A4D7E1F3C35D5A4A9C2E1B0C0FFEE";

    private const string NewHash = "70AFDB530DD7CD04D1C36E142CC523A56E738DE8";

    private const string Master = "South.Park.S29E03.Episode.3.NoMercy.m3u8";

    private static readonly EpisodeKey S29E03 = new(2190, 29, 3);

    /// <summary>When ViETNAM's capture was taken on.</summary>
    private static readonly DateTimeOffset Delivered = new(2026, 10, 15, 3, 5, 0, TimeSpan.Zero);

    /// <summary>When its encode wrote the episode into the library.</summary>
    private static readonly DateTimeOffset Encoded = Delivered.AddMinutes(30);

    /// <summary>When CAKES's release was taken on to replace it.</summary>
    private static readonly DateTimeOffset Replacing = new(2026, 10, 15, 6, 10, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-replace-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// <para>
    /// <strong>Once the new release is downloaded and staged, the old episode's encode goes to the recycle
    /// area, and then the encode is asked for — in that order.</strong> Every file of the episode's folder the
    /// delivery's encode wrote, the master playlist first: once it is gone the server encodes the episode afresh
    /// whatever else is left (its reconciler reads a missing master playlist as an output to write in full), so
    /// a recycling that stops half way can never leave an encode skipped over a half-moved episode.
    /// </para>
    /// <para>
    /// <strong>And nothing else.</strong> Not S29E02's folder beside it, not a file in the show's folder, not
    /// what is already in the folder's own recycle area, and not a file in the episode's folder from before the
    /// delivery — that one the plugin's grab did not cause. Nothing is deleted or moved by any other means.
    /// </para>
    /// <para>
    /// Sabotage: asking for the encode without recycling first encodes over the old episode, which the server
    /// skips.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheOldEpisodesEncodeIsRecycledAndThenTheEncodeIsAskedFor()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);

        await Transfers(Holding(Finished()), grabs, server, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(
            [
                $"{EpisodeFolder}/{Master}",
                $"{EpisodeFolder}/.nomercy.json",
                $"{EpisodeFolder}/subtitles/South.Park.S29E03.Episode.3.NoMercy.eng.full.vtt",
                $"{EpisodeFolder}/video_1920x1080_SDR/video_1920x1080_SDR-0000.ts",
                $"{EpisodeFolder}/video_1920x1080_SDR/video_1920x1080_SDR.m3u8",
            ],
            writer.Recycled.Select(one => one.Path));

        Assert.All(writer.Recycled, one => Assert.Equal(0, one.EncodesAskedBefore));
        Assert.NotNull(server.Encoder.Job);
        Assert.Empty(writer.DeletedOrMoved);

        StoredDownload replacing = Assert.Single(await grabs.EveryAsync(CancellationToken.None), one => one.InfoHash == NewHash);

        Assert.Equal(GrabState.Dispatched, replacing.State);
        Assert.NotNull(replacing.RecycledAt);

    }

    /// <remarks>
    /// <strong>Recycled once, however often the encode is asked for.</strong> An encode the server refuses is
    /// asked for again by the next look, from the staged file; the recycling was written down before the first
    /// ask, so the next look recycles nothing — by then the folder may hold what the new encode is writing.
    /// The double moves the files as the server does, so the second look finds none of the old ones left; what
    /// it would find is what the new encode wrote, which is newer than the replacement's grab and so never the
    /// old delivery's.
    /// </remarks>
    [Fact]
    public async Task AnEncodeAskedForAgainRecyclesNothingASecondTime()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);

        server.Encoder.Refusal = "the server is still starting";
        await Transfers(Holding(Finished()), grabs, server, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(5, writer.Recycled.Count);
        Assert.Null(server.Encoder.Job);

        server.Encoder.Refusal = null;
        await Transfers(Holding(Finished()), grabs, server, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.NotNull(server.Encoder.Job);
        Assert.Equal(5, writer.Recycled.Count);
    }

    /// <remarks>
    /// <strong>An episode the owner put in the library is never recycled.</strong> Its file in the library is
    /// older than the plugin's delivery, so the delivery did not cause it: nothing is recycled, nothing is
    /// encoded over it, and why is said. Sabotage: recycling whatever the folder holds takes the owner's file.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeTheOwnerPutInTheLibraryIsNeverRecycled()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library(written: Delivered.AddDays(-30));

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);
        Transfers transfers = Transfers(Holding(Finished()), grabs, server, writer);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(writer.Recycled);
        Assert.Null(server.Encoder.Job);
        Assert.True(File.Exists(Path.Combine(Intake, Staged)), "The new video did not wait in the intake folder.");
        Assert.Contains("not the plugin's", transfers.FailureOf(NewHash), StringComparison.Ordinal);
    }

    /// <remarks>
    /// <strong>A failed or unfinished new download leaves the old episode untouched.</strong> Nothing is staged,
    /// so nothing is recycled and nothing is encoded, and every file of the old episode is where it was.
    /// Sabotage, seen red on 16 October 2026: staging a replacement before every byte is on disk
    /// (<c>Recovery.Plan</c>) recycles the old episode for half a download; letting an errored torrent past
    /// <c>Transfers.FailedAsync</c> recycles it for one the client gave up on.
    /// </remarks>
    [Theory]
    [InlineData(TorrentState.Error)]
    [InlineData(TorrentState.Downloading)]
    public async Task AFailedOrUnfinishedDownloadLeavesTheOldEpisodeUntouched(TorrentState state)
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);

        TorrentStatus status = Finished() with
        {
            State = state,
            BytesDone = state == TorrentState.Downloading ? 300_000_000 : 900_000_000,
            Error = state == TorrentState.Error ? "no peer sent its metadata" : null,
        };

        await Transfers(Holding(status), grabs, server, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(writer.Recycled);
        Assert.Null(server.Encoder.Job);
        Assert.True(File.Exists($"{EpisodeFolder}/{Master}"));
    }

    /// <remarks>
    /// <para>
    /// <strong>Not granted, or refused: nothing recycled, nothing encoded, the staged file waits, and why is said
    /// once.</strong> A plugin the owner granted no library to write in gets no writer from the server at all; a
    /// server that refuses a path throws. Either way the new video stays in the intake folder, the Downloads row
    /// says why, and the second look tries again without saying it a second time.
    /// </para>
    /// <para>
    /// Sabotage: encoding when recycling was refused encodes over the old episode; saying the reason on every
    /// look fills the History page with it.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("not granted")]
    [InlineData("refused")]
    [InlineData("not writable")]
    public async Task NotGrantedOrRefusedRecyclesNothingEncodesNothingAndSaysWhyOnce(string server)
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        FakeProvider host = new();
        RecordingLibraryWriter? writer = server == "not granted"
            ? null
            : new(host.Encoder)
            {
                Refusal = server == "refused" ? "the path is not inside a library this plugin has been granted write access to" : null,
                Writable = server != "not writable",
            };

        Transfers transfers = Transfers(Holding(Finished()), grabs, host, writer);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(writer?.Recycled ?? []);
        Assert.Null(host.Encoder.Job);
        Assert.True(File.Exists(Path.Combine(Intake, Staged)), "The new video did not wait in the intake folder.");
        Assert.True(File.Exists($"{EpisodeFolder}/{Master}"));
        Assert.Equal(GrabState.Staged, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        string? why = transfers.FailureOf(NewHash);

        Assert.NotNull(why);
        Assert.Contains("nothing is encoded over the old", why, StringComparison.Ordinal);
        Assert.Single(host.Journal.Snapshot().History, entry => entry.Stage == ActivityStage.Dispatch && entry.Detail == why);
        Assert.Single(host.Log.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Warning && entry.Line.Contains(why, StringComparison.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// <strong>Only what the encoder writes is recycled.</strong> The episode's folder is the encode's, but not
    /// only the encode's: a metadata tool writes an <c>.nfo</c> beside it, the owner keeps extras in it. Written
    /// in the same hours as the encode, they passed for the delivery's. The encoder's own output, read off
    /// media-server <c>dev</c> on 16 October 2026 (<c>FinalizeStage</c>, <c>BuildStage</c>,
    /// <c>MediaBlueprintWriter</c>, <c>HlsOnDiskPlanReconstructor</c>, <c>FontExtractor</c>): the master
    /// playlist the library names, <c>.nomercy.json</c>, <c>manifest.json</c>, <c>chapters.vtt</c>,
    /// <c>fonts.json</c>, the sprite <c>thumbs_{w}x{h}.webp</c> and its <c>.vtt</c>, and the folders
    /// <c>video_*</c>, <c>audio_*</c>, <c>subtitles</c> and <c>fonts</c>.
    /// </para>
    /// <para>Sabotage: recycling every file written in the window takes the owner's extras with it.</para>
    /// </remarks>
    [Fact]
    public async Task OnlyWhatTheEncoderWroteIsRecycled()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        string[] encoderOutput =
        [
            $"{EpisodeFolder}/manifest.json",
            $"{EpisodeFolder}/chapters.vtt",
            $"{EpisodeFolder}/fonts.json",
            $"{EpisodeFolder}/thumbs_320x180.webp",
            $"{EpisodeFolder}/thumbs_320x180.vtt",
            $"{EpisodeFolder}/audio_eng_aac/audio_eng_aac.m3u8",
            $"{EpisodeFolder}/fonts/Arial.ttf",
        ];
        string[] notTheEncoders =
        [
            $"{EpisodeFolder}/South.Park.S29E03.nfo",
            $"{EpisodeFolder}/extras/Behind.The.Scenes.mkv",
        ];

        foreach (string path in encoderOutput.Concat(notTheEncoders))
        {
            Write(path, Encoded);
        }

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);

        await Transfers(Holding(Finished()), grabs, server, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        IReadOnlyList<string> recycled = [.. writer.Recycled.Select(one => one.Path)];

        Assert.All(encoderOutput, path => Assert.Contains(path, recycled));
        Assert.All(notTheEncoders, path =>
        {
            Assert.DoesNotContain(path, recycled);
            Assert.True(File.Exists(path), $"{path} is not the encoder's and was taken anyway.");
        });
        Assert.NotNull(server.Encoder.Job);
    }

    /// <remarks>
    /// <para>
    /// <strong>An encode made after the plugin's delivery is not the plugin's.</strong> The delivery was done
    /// with its master playlist written down; the owner then encoded the episode by hand while the replacement
    /// was being waited for, which wrote a new playlist in the same folder and inside the same hours. Taken by
    /// the time window alone it passed for the delivery's and went to the recycle area. The playlist is
    /// compared with what was written down, so nothing moves, nothing is encoded, and why is said.
    /// </para>
    /// <para>Sabotage: recycling on the time window alone takes the owner's own encode.</para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeMadeAfterThePluginsDeliveryIsNeverRecycled()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();
        await grabs.EncodedAsync(OldHash, new Dictionary<EpisodeKey, EncodedStamp> { [S29E03] = Stamp($"{EpisodeFolder}/{Master}") }, CancellationToken.None);

        // The owner's own encode, an hour after the plugin's and before the replacement was taken on.
        Write($"{EpisodeFolder}/{Master}", Encoded.AddHours(1));

        FakeProvider host = new();
        RecordingLibraryWriter writer = new(host.Encoder);
        Transfers transfers = Transfers(Holding(Finished()), grabs, host, writer);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(writer.Recycled);
        Assert.Null(host.Encoder.Job);
        Assert.True(File.Exists($"{EpisodeFolder}/{Master}"));
        Assert.Contains("is not the one the plugin delivered", transfers.FailureOf(NewHash), StringComparison.Ordinal);
    }

    /// <remarks>
    /// <strong>An encode that is still the delivery's, as written down, is recycled.</strong> Sabotage: refusing
    /// whenever a stamp is kept leaves every replacement waiting.
    /// </remarks>
    [Fact]
    public async Task AnEncodeThatIsStillTheDeliverysIsRecycled()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();
        await grabs.EncodedAsync(OldHash, new Dictionary<EpisodeKey, EncodedStamp> { [S29E03] = Stamp($"{EpisodeFolder}/{Master}") }, CancellationToken.None);

        FakeProvider host = new();
        RecordingLibraryWriter writer = new(host.Encoder);

        await Transfers(Holding(Finished()), grabs, host, writer).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(5, writer.Recycled.Count);
        Assert.NotNull(host.Encoder.Job);
    }

    private static EncodedStamp Stamp(string path)
    {
        FileInfo file = new(path);

        return new(file.LastWriteTimeUtc, file.Length);
    }

    /// <remarks>
    /// <strong>A file of the same name already in the recycle area moves nothing.</strong> The server recycles
    /// under the file's own name and never over one already there; asked anyway, it refuses half way, with the
    /// master playlist gone and the rest of the old encode still in place. So it is asked of every file first,
    /// nothing moves, and why is said once. Sabotage: moving without asking first leaves the master playlist in
    /// the recycle area and the segments in the library.
    /// </remarks>
    [Fact]
    public async Task ASecondCopyInTheRecycleAreaMovesNothingAndSaysWhy()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        // A copy of one of the files the encode wrote, put there by an earlier recycling.
        Write($"{EpisodeFolder}/video_1920x1080_SDR/.recycled/video_1920x1080_SDR-0000.ts", Encoded);

        FakeProvider host = new();
        RecordingLibraryWriter writer = new(host.Encoder);
        Transfers transfers = Transfers(Holding(Finished()), grabs, host, writer);

        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);
        await transfers.TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(writer.Recycled);
        Assert.True(File.Exists($"{EpisodeFolder}/{Master}"), "The master playlist was recycled although the rest could not be.");
        Assert.Null(host.Encoder.Job);
        Assert.Equal(GrabState.Staged, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        string? why = transfers.FailureOf(NewHash);

        Assert.NotNull(why);
        Assert.Contains("video_1920x1080_SDR-0000.ts is already in the recycle area", why, StringComparison.Ordinal);
        Assert.Single(host.Journal.Snapshot().History, entry => entry.Stage == ActivityStage.Dispatch && entry.Detail == why);
    }

    /// <remarks>
    /// <para>
    /// <strong>The replacement is done only on the new encode itself.</strong> The episode had a file all along,
    /// so the library having it proves nothing; and the server's "finished" is not proof either — media-server
    /// #599 called an encode finished while it was still running. The proof is the file the library names for
    /// the episode, written after the old one went to the recycle area. Until it is there the staged video is
    /// kept: deleted early, the new encode has nothing left to read.
    /// </para>
    /// <para>
    /// Sabotage: closing on the library having the episode deletes it on the first look; closing on the server
    /// saying "finished" deletes it while the folder still holds no new playlist.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AReplacementIsDoneOnlyOnTheNewEncodeItself()
    {
        GrabRepository grabs = await Grabs();
        await DeliveredAndReplacing(grabs);
        Library();

        FakeProvider server = new();
        RecordingLibraryWriter writer = new(server.Encoder);
        SaysOfTheJob says = new();

        await Transfers(Holding(Finished()), grabs, server, writer, says).TickAsync(Incomplete, Intake, CancellationToken.None);

        // The server says nothing of the job yet. For a missing episode the library having it would be the
        // proof; for this one it is the old file.
        says.State = null;
        await Transfers(Holding(Finished()), grabs, server, writer, says).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(GrabState.Dispatched, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);
        Assert.True(File.Exists(Path.Combine(Intake, Staged)), "The staged video went before anything said its encode was done.");

        // The server says it finished, and the folder holds no new playlist.
        says.State = EncodeJobState.Finished;
        await Transfers(Holding(Finished()), grabs, server, writer, says).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Equal(GrabState.Dispatched, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);
        Assert.True(File.Exists(Path.Combine(Intake, Staged)), "The staged video went on the server's word alone.");

        // The new encode's playlist, written after the old one was recycled.
        DateTimeOffset recycled = Assert.Single(await grabs.EveryAsync(CancellationToken.None), one => one.InfoHash == NewHash).RecycledAt!.Value;
        Write($"{EpisodeFolder}/{Master}", recycled.AddMinutes(20));

        await Transfers(Holding(Finished()), grabs, server, writer, says).TickAsync(Incomplete, Intake, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // And done with the playlist its encode has written down, for the next replacement to be held to.
        StoredDownload done = Assert.Single(await grabs.EveryAsync(CancellationToken.None), one => one.InfoHash == NewHash);

        Assert.Equal(Stamp($"{EpisodeFolder}/{Master}"), done.Encoded[S29E03]);
    }

    /// <summary>The server's word about the one job asked for, as a test sets it.</summary>
    private sealed class SaysOfTheJob : IEncoderSays
    {
        public EncodeJobState? State { get; set; }

        public Task<EncodeJob?> AboutAsync(string jobId, CancellationToken ct)
        {
            return Task.FromResult(State is EncodeJobState state ? new EncodeJob(state, null) : null);
        }
    }

    private string Incomplete => Path.Combine(_root, "incomplete");

    private string Intake => Path.Combine(_root, "intake");

    /// <summary>The library folder, with forward slashes, as the server's folder row holds it.</summary>
    private string LibraryFolder => Path.Combine(_root, "TV.Shows").Replace('\\', '/');

    private string EpisodeFolder => $"{LibraryFolder}/South.Park.(1997)/South.Park.S29E03";

    private static string Staged => EpisodeName.For("South Park", 1997, S29E03, "1080p", ".mkv");

    /// <summary>
    /// The library on disk, as the encoder writes it, and the file the server registered for S29E03.
    /// </summary>
    /// <param name="written">When the encode's files were written; the delivery's encode by default.</param>
    private void Library(DateTimeOffset? written = null)
    {
        DateTimeOffset at = written ?? Encoded;

        Write($"{EpisodeFolder}/{Master}", at);

        // The blueprint the encoder stamps beside it (MediaBlueprintWriter.FileName), which sorts ahead of it.
        Write($"{EpisodeFolder}/.nomercy.json", at);
        Write($"{EpisodeFolder}/video_1920x1080_SDR/video_1920x1080_SDR.m3u8", at);
        Write($"{EpisodeFolder}/video_1920x1080_SDR/video_1920x1080_SDR-0000.ts", at);
        Write($"{EpisodeFolder}/subtitles/South.Park.S29E03.Episode.3.NoMercy.eng.full.vtt", at);

        // Already in the recycle area beside it, from something earlier: not moved a second time. Under a
        // name of its own — one of the same name as a file being recycled refuses the recycling
        // (ASecondCopyInTheRecycleAreaMovesNothingAndSaysWhy).
        Write($"{EpisodeFolder}/.recycled/South.Park.S29E03.Episode.3.Earlier.m3u8", at);

        // In the folder from before the delivery, so not the delivery's.
        Write($"{EpisodeFolder}/folder.jpg", Delivered.AddDays(-30));

        // Another episode, and the show's own folder.
        Write($"{LibraryFolder}/South.Park.(1997)/South.Park.S29E02/South.Park.S29E02.Billionaire.Weenietown.NoMercy.m3u8", at);
        Write($"{LibraryFolder}/South.Park.(1997)/poster.jpg", at);
    }

    private static void Write(string path, DateTimeOffset at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "#EXTM3U");
        File.SetLastWriteTimeUtc(path, at.UtcDateTime);
    }

    /// <summary>The ViETNAM delivery, done; and CAKES's grab that replaces it, downloaded into the incomplete folder.</summary>
    private async Task DeliveredAndReplacing(GrabRepository grabs)
    {
        await grabs.RecordAsync(S29E03, "South Park", Vietnam, "TorrentBay", OldHash, $"magnet:?xt=urn:btih:{OldHash}", [S29E03], Delivered, CancellationToken.None, named: false);
        await grabs.StateAsync(OldHash, GrabState.Done, CancellationToken.None);

        await grabs.RecordAsync(S29E03, "South Park", Cakes, "LimeTorrents", NewHash, $"magnet:?xt=urn:btih:{NewHash}", [S29E03], Replacing, CancellationToken.None, named: true, replaces: OldHash);

        Directory.CreateDirectory(Incomplete);

        await using FileStream video = File.Create(Path.Combine(Incomplete, $"{Cakes}.mkv"));

        video.SetLength(900_000_000);
    }

    private static TorrentStatus Finished()
    {
        return new(
            NewHash,
            Cakes,
            TorrentState.Finished,
            BytesDone: 900_000_000,
            BytesTotal: 900_000_000,
            DownloadRateBytesPerSecond: 0,
            Peers: 0,
            Seeds: 0,
            Ratio: 1.0,
            Eta: null,
            Error: null);
    }

    private static StandingEngine Holding(TorrentStatus status)
    {
        return new StandingEngine().Holding(status, new TorrentFile($"{Cakes}.mkv", 900_000_000));
    }

    private Transfers Transfers(
        ITorrentEngine engine,
        GrabRepository grabs,
        FakeProvider server,
        RecordingLibraryWriter? writer,
        IEncoderSays? says = null)
    {
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library(TelevisionLibrary, "Series", "tv")
            .Show(2190, "South Park", TelevisionLibrary, year: 1997, folder: "/South.Park.(1997)")
            .Episode(2190, 29, 3, title: "Episode 3", hasFile: true, id: 7292377)

            // HostFolder + Folder + Filename, as the owner's server answers the row the encoder registered.
            .File(2190, 29, 3, $"{EpisodeFolder}/South.Park.(1997)/South.Park.S29E03/{Master}");

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
            says,
            new Recycler(writer));
    }

    private async Task<GrabRepository> Grabs()
    {
        Store database = TestStore.In(_root);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
