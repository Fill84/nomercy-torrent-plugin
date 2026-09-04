using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The torrent client's shell: its lifetime, its port, and what it says about
/// a torrent it has taken on.
/// </summary>
public class BittorrentEngineTests : IDisposable
{
    /// <remarks>
    /// Started once and stopped once, whatever ticks in between. The client
    /// owns sockets and a port mapping; a second one would bind a port the
    /// first already has and report it as somebody else's.
    /// </remarks>
    [Fact]
    public void StartingItFourTimesBindsThePortOnce()
    {
        // Nought, so the operating system picks the number: what matters is
        // that four ticks leave one port and not four.
        using BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, null, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

        engine.Start();

        int? first = engine.Port;

        for (int tick = 0; tick < 3; tick++)
        {
            engine.Start();
        }

        Assert.NotNull(first);
        Assert.Equal(first, engine.Port);
        Assert.Null(engine.Failure);
    }

    /// <remarks>
    /// And a host is entitled to dispose twice. The second one is not allowed
    /// to throw on the way out of a shutdown.
    /// </remarks>
    [Fact]
    public void DisposingItTwiceIsSafe()
    {
        BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, null, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

        engine.Start();
        engine.Dispose();
        engine.Dispose();
    }

    /// <remarks>
    /// A port something else has is reported with its number and the client
    /// carries on. Taking the plugin down over it would cost the owner
    /// everything else it does, and outgoing connections still work.
    /// </remarks>
    [Fact]
    public void APortInUseIsReportedWithItsNumberAndDoesNotThrow()
    {
        // Held for the whole test: a port this process released is one another
        // test can take between the two lines.
        using ListenSockets held = ListenSockets.Bind(0);
        int port = held.Port;

        ActivityJournal journal = new();

        using BittorrentEngine engine = new(
            port,
            Timeout,
            Stall,
            Together,
            Seeding,
            0,
            0,
            null,
            journal,
            new CapturingLogger(),
            new SilentTrackers(),
            new NoPeers());

        engine.Start();

        Assert.Null(engine.Port);
        Assert.Contains(port.ToString(), engine.Failure!, StringComparison.Ordinal);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed
                     && entry.Detail!.Contains(port.ToString(), StringComparison.Ordinal));
    }

    /// <remarks>
    /// A magnet just taken on is fetching its metadata, and that is not a shade
    /// of downloading: it has no name, no files and no size until the metadata
    /// arrives, and "nought per cent downloading" makes a torrent that will
    /// never resolve look like one about to start.
    /// </remarks>
    [Fact]
    public async Task AMagnetJustAddedIsFetchingItsMetadataAndNotDownloading()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", handle.InfoHash);

        TorrentStatus status = Assert.Single(await engine.StatusAsync(CancellationToken.None));

        Assert.Equal(TorrentState.FetchingMetadata, status.State);
        Assert.NotEqual(TorrentState.Downloading, status.State);

        // And every number says what it is rather than being drawn as nought.
        Assert.Null(status.Ratio);
        Assert.Null(status.Eta);
        Assert.Equal(0, status.BytesDone);

        // No file list at all until the metadata arrives: inventing one from
        // the name is how the wrong file gets staged.
        Assert.Empty(await engine.FilesAsync(handle.InfoHash, CancellationToken.None));
    }

    /// <remarks>
    /// A magnet taken on is a torrent this client is really working on, not a
    /// row in a list. For a whole sprint <c>AddAsync</c> recorded the hash and
    /// stopped: nothing announced, nothing dialled, and every page above it was
    /// correct about a client that would never finish anything.
    /// </remarks>
    [Fact]
    public async Task AMagnetTakenOnIsAnnouncedToItsTrackers()
    {
        SilentTrackers trackers = new();

        using BittorrentEngine engine = new(
            0,
            Timeout,
            Stall,
            Together,
            Seeding,
            0,
            0,
            null,
            new ActivityJournal(),
            new CapturingLogger(),
            trackers,
            new NoPeers());

        engine.Start();

        await engine.AddAsync(Request, CancellationToken.None);
        await trackers.Asked.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <remarks>
    /// <para>
    /// A peer that dials in is taken up. Every connection this client had ever
    /// made it made itself, so it seeded to nobody who found it and never met
    /// the half of a swarm behind a router of its own — and the listening
    /// socket it binds on startup had nothing behind it.
    /// </para>
    /// <para>
    /// Over the loopback, dialled by this client's own dialler, which is as
    /// close to a real peer as a test on this machine can get.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APeerThatDialsInIsTakenUp()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        using BittorrentEngine engine = new(
            0,
            Timeout,
            Stall,
            Together,
            Seeding,
            0,
            0,
            null,
            new ActivityJournal(),
            new CapturingLogger(),
            new SilentTrackers(),
            new NoPeers());

        engine.Start();

        TorrentHandle handle = await engine.AddAsync(Request, stopping.Token);

        PeerConnection? dialled = await new SocketPeerDialler(TimeSpan.FromSeconds(20)).DialAsync(
            new(System.Net.IPAddress.Loopback, engine.Port!.Value),
            Convert.FromHexString(handle.InfoHash),
            PeerIdentity.New(),
            pieces: 0,
            stopping.Token);

        Assert.NotNull(dialled);

        using (dialled)
        {
            await Until(
                async () => (await engine.StatusAsync(stopping.Token))[0].Peers == 1,
                stopping.Token);
        }
    }

    /// <summary>Waits for something the accept loop does on its own thread.</summary>
    /// <remarks>
    /// Polled rather than slept on: a fixed wait is either a test that fails on
    /// a busy machine or one that costs a second every run.
    /// </remarks>
    private static async Task Until(Func<Task<bool>> what, CancellationToken ct)
    {
        while (!await what())
        {
            ct.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    /// <remarks>
    /// Paused is its own state and stays that way until something resumes it.
    /// </remarks>
    [Fact]
    public async Task APausedTorrentIsPausedAndAResumedOneIsNot()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        await engine.PauseAsync(handle.InfoHash, CancellationToken.None);

        Assert.Equal(TorrentState.Paused, (await engine.StatusAsync(CancellationToken.None))[0].State);

        await engine.ResumeAsync(handle.InfoHash, CancellationToken.None);

        Assert.NotEqual(TorrentState.Paused, (await engine.StatusAsync(CancellationToken.None))[0].State);
    }

    /// <remarks>
    /// The same torrent offered twice is one torrent with more trackers, which
    /// is the whole reason every indexer is asked. Two rows for one info hash
    /// would be two clients downloading the same file into the same folder.
    /// </remarks>
    [Fact]
    public async Task TheSameHashAddedTwiceIsOneTorrent()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);
        await engine.AddAsync(Request with { Trackers = ["udp://elsewhere.example:6969/announce"] }, CancellationToken.None);

        Assert.Single(await engine.StatusAsync(CancellationToken.None));

        // And it is one torrent with everybody's trackers: the magnet's own,
        // the ones the first site merged, and the ones the second brought.
        Assert.Equal(
            ["udp://elsewhere.example:6969/announce", "udp://one.example:80", "udp://two.example:80"],
            engine.TrackersOf(handle.InfoHash).Order());
    }

    /// <remarks>
    /// Removed means gone from the client. What happens to the files is the
    /// caller's business and is asked for by name.
    /// </remarks>
    [Fact]
    public async Task ARemovedTorrentIsGone()
    {
        using BittorrentEngine engine = Started();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        await engine.RemoveAsync(handle.InfoHash, deleteFiles: false, CancellationToken.None);

        Assert.Empty(await engine.StatusAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Anything that is neither a magnet nor a torrent is refused by name. The
    /// name is the whole of it: "the source is not supported" leaves the owner
    /// looking at a page with no idea which of the things they pasted was
    /// wrong.
    /// </remarks>
    [Fact]
    public async Task ASourceThatIsNeitherAMagnetNorATorrentIsRefusedByName()
    {
        using BittorrentEngine engine = Started();

        NotSupportedException refused = await Assert.ThrowsAsync<NotSupportedException>(
            () => engine.AddAsync(
                Request with { Source = "ftp://example.test/thing.bin" },
                CancellationToken.None));

        Assert.Contains("thing.bin", refused.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// A <c>.torrent</c> is taken on with everything already known: its name,
    /// its size and its files, without a peer having been asked for anything.
    /// docs/08-ui.md requires it — <c>AddTorrent</c> takes a magnet or a
    /// <c>.torrent</c> — and it is also what lets one instance of this client
    /// seed to another.
    /// </para>
    /// <para>
    /// A file rather than an address, because that is what an owner has when
    /// they have downloaded one from a site that offers nothing else.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentFileIsTakenOnWithItsMetadataAlreadyKnown()
    {
        using BittorrentEngine engine = Started();

        string file = Path.Combine(_folder, "archive.torrent");

        Directory.CreateDirectory(_folder);
        File.WriteAllBytes(file, Fixture("archive-multifile.torrent"));

        TorrentHandle handle = await engine.AddAsync(
            Request with { Source = file, DownloadFolder = _folder },
            CancellationToken.None);

        TorrentStatus status = Assert.Single(await engine.StatusAsync(CancellationToken.None));

        Assert.NotEqual(TorrentState.FetchingMetadata, status.State);
        Assert.Equal(198588270, status.BytesTotal);
        Assert.NotNull(status.Name);
        Assert.NotEmpty(await engine.FilesAsync(handle.InfoHash, CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong><c>MaxConcurrentDownloads</c> is a limit and not a note.</strong>
    /// It was read from the Settings page and passed to nothing at all: on
    /// 22 August 2026 the owner's client had sixteen torrents dialling at once
    /// over one line, and fifteen of them never got past fetching metadata.
    /// </para>
    /// <para>
    /// The ones over the limit wait, and they say so — queued is its own state,
    /// because a torrent the owner stopped and a torrent about to start on its
    /// own are not the same thing to look at. Oldest first, so the queue is the
    /// order they were grabbed in.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NoMoreThanTheLimitRunAtOnce()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));

        using BittorrentEngine engine = new(
            0,
            TimeSpan.FromHours(6),
            TimeSpan.FromHours(6),
            3,
            Seeding,
            0,
            0,
            null,
            new ActivityJournal(clock),
            new CapturingLogger(),
            new SilentTrackers(),
            new NoPeers(),
            clock);

        engine.Start();

        for (int which = 0; which < 5; which++)
        {
            await engine.AddAsync(Another(which), CancellationToken.None);

            // A second apart, so the order they were grabbed in is a fact and
            // not whatever a dictionary hands back.
            clock.Advance(TimeSpan.FromSeconds(1));
        }

        IReadOnlyList<TorrentStatus> running = await engine.StatusAsync(CancellationToken.None);

        Assert.Equal(5, running.Count);
        Assert.Equal(3, running.Count(one => one.State == TorrentState.FetchingMetadata));
        Assert.Equal(2, running.Count(one => one.State == TorrentState.Queued));

        // And the two waiting are the two grabbed last.
        Assert.All(
            running.Where(one => one.State == TorrentState.Queued),
            one => Assert.Contains(one.InfoHash, new[] { HashOf(3), HashOf(4) }, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>One more magnet, with an info hash of its own.</summary>
    private static TorrentRequest Another(int which)
    {
        return Request with
        {
            Source = $"magnet:?xt=urn:btih:{HashOf(which)}&dn=Silo+S03E0{which}&tr=udp%3A%2F%2Fone.example%3A80",
        };
    }

    private static string HashOf(int which)
    {
        return new string((char)('A' + which), 40);
    }

    /// <remarks>
    /// <para>
    /// <strong>A client that has the metadata never asks for it again.</strong>
    /// Every torrent here is added from a magnet, which carries a hash and no
    /// file list, so without this the swarm is asked again after every restart
    /// — and a swarm that has gone quiet cannot answer. The torrent then times
    /// out and is given up on however complete it is on disk.
    /// </para>
    /// <para>
    /// On 23 August 2026 the owner restarted with twenty-three finished
    /// downloads on disk, and thirty-three grabs were failed for want of a file
    /// list for files that were already there.
    /// </para>
    /// <para>
    /// Nothing here has a swarm at all: the peer dialler answers nobody, which
    /// is the whole point. The file list has to come off the disk or not at
    /// all.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMagnetWhoseMetadataWasWrittenDownNeedsNoPeerToKnowItsFiles()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-remembered-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            ResumeKeeper keeping = new(folder, TimeSpan.FromSeconds(1), TimeProvider.System);
            byte[] file = Fixture("archive-multifile.torrent");
            TorrentMetadata torrent = TorrentMetadata.Read(file);

            keeping.Remember(torrent.InfoHash, Info(file));

            using BittorrentEngine engine = new(
                0,
                Timeout,
                Stall,
                Together,
                Seeding,
                0,
                0,
                null,
                new ActivityJournal(),
                new CapturingLogger(),
                new SilentTrackers(),
                new NoPeers(),
                null,
                keeping);

            engine.Start();

            await engine.AddAsync(
                new(
                    $"magnet:?xt=urn:btih:{torrent.InfoHash}&dn=whatever",
                    [],
                    folder,
                    torrent.TotalLength),
                CancellationToken.None);

            IReadOnlyList<TorrentFile> files = await engine.FilesAsync(torrent.InfoHash, CancellationToken.None);

            Assert.Equal(torrent.Files.Count, files.Count);

            // And it is never waiting on a swarm for what it already knows.
            // This fixture is a book rather than an episode, so it is refused
            // for holding no video — which is a decision that could only be
            // made because the file list was there.
            TorrentStatus status = Assert.Single(await engine.StatusAsync(CancellationToken.None));

            Assert.DoesNotContain("metadata", status.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>The info dictionary out of a whole .torrent file.</summary>
    private static byte[] Info(byte[] file)
    {
        BencodeDocument document = Bencode.Read(file);

        return [.. file.AsSpan(document.InfoStart!.Value, document.InfoLength!.Value)];
    }

    /// <summary>
    /// <c>MetadataTimeoutMinutes</c>' own default, five minutes. The tests that
    /// are not about the clock never reach it.
    /// </summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(new ClientLimits().MetadataTimeoutMinutes);

    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(new ClientLimits().StallMinutes);

    private static readonly int Together = new ClientLimits().MaxConcurrentDownloads;

    private static readonly SeedLimit Seeding = new(
        new ClientLimits().SeedRatio,
        TimeSpan.FromHours(new ClientLimits().SeedHours));

    private static readonly TorrentRequest Request = new(
        "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3&dn=Silo+S03E06&tr=udp%3A%2F%2Fone.example%3A80",
        ["udp://two.example:80"],
        "C:\\downloads",
        4_388_742_440);

    /// <remarks>
    /// <para>
    /// A magnet with no peer in the swarm that has the metadata never resolves,
    /// and until it is failed the episode it was grabbed for is never looked
    /// for again — it has been grabbed, as far as anything else can tell. 0.3.4
    /// left those sitting at "fetching metadata" for as long as the server ran.
    /// </para>
    /// <para>
    /// Not a minute early: the limit is the limit, and failing a torrent whose
    /// metadata was about to arrive throws away a real download.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AMagnetWhoseMetadataNeverArrivesFailsWhenTheLimitPasses()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        ActivityJournal journal = new(clock);

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, null, journal, new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

        engine.Start();

        await engine.AddAsync(Request, CancellationToken.None);

        clock.Advance(TimeSpan.FromSeconds(299));

        Assert.Equal(TorrentState.FetchingMetadata, (await engine.StatusAsync(CancellationToken.None))[0].State);
        Assert.Null((await engine.StatusAsync(CancellationToken.None))[0].Error);

        clock.Advance(TimeSpan.FromSeconds(1));

        TorrentStatus failed = (await engine.StatusAsync(CancellationToken.None))[0];

        Assert.Equal(TorrentState.Error, failed.State);
        Assert.Contains("5 minutes", failed.Error!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Transfers ticks every minute. A failure said once a tick is a journal
    /// nobody can read by the following morning.
    /// </remarks>
    [Fact]
    public async Task TheFailureIsSaidOnceAndNotOnceATick()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));
        ActivityJournal journal = new(clock);

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, null, journal, new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

        engine.Start();

        await engine.AddAsync(Request, CancellationToken.None);

        for (int tick = 0; tick < 10; tick++)
        {
            clock.Advance(TimeSpan.FromMinutes(1));

            await engine.StatusAsync(CancellationToken.None);
        }

        Assert.Single(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed);
    }

    /// <remarks>
    /// A torrent resumed after the limit had passed is asked for again, and the
    /// clock starts with it. Keeping the old one would fail it on the very next
    /// tick without a single peer having been asked.
    /// </remarks>
    [Fact]
    public async Task ResumingAFailedMagnetStartsItsClockAgain()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, null, new ActivityJournal(clock), new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

        engine.Start();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(TorrentState.Error, (await engine.StatusAsync(CancellationToken.None))[0].State);

        await engine.ResumeAsync(handle.InfoHash, CancellationToken.None);

        TorrentStatus resumed = (await engine.StatusAsync(CancellationToken.None))[0];

        Assert.Equal(TorrentState.FetchingMetadata, resumed.State);
        Assert.Null(resumed.Error);
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent nothing is happening to is given up on.</strong> No
    /// progress and no peer for <c>StallMinutes</c>: the reason is recorded
    /// against the grab and the episode goes back to being missing, rather than
    /// the row sitting on the Downloads page for as long as the server runs.
    /// </para>
    /// <para>
    /// <c>StallWatch</c> was written in Sprint 6 and wired to nothing at all,
    /// which is how fifteen of the owner's torrents came to sit at nought peers
    /// indefinitely on 22 August 2026. The metadata clock is set long here so
    /// that what fires is this rule and not that one.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentWithNoPeersAndNoProgressIsGivenUpOn()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 22, 12, 0, 0, TimeSpan.Zero));

        using BittorrentEngine engine = new(
            0,
            TimeSpan.FromHours(6),
            TimeSpan.FromMinutes(30),
            Together,
            Seeding,
            0,
            0,
            null,
            new ActivityJournal(clock),
            new CapturingLogger(),
            new SilentTrackers(),
            new NoPeers(),
            clock);

        engine.Start();

        await engine.AddAsync(Request, CancellationToken.None);

        // The first reading starts its clock, and it has to be taken before the
        // wait rather than after it.
        Assert.Equal(TorrentState.FetchingMetadata, (await engine.StatusAsync(CancellationToken.None))[0].State);

        clock.Advance(TimeSpan.FromMinutes(29));

        Assert.Equal(TorrentState.FetchingMetadata, (await engine.StatusAsync(CancellationToken.None))[0].State);

        clock.Advance(TimeSpan.FromMinutes(2));

        TorrentStatus given = (await engine.StatusAsync(CancellationToken.None))[0];

        Assert.Equal(TorrentState.Error, given.State);
        Assert.Contains("30", given.Error!, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Paused is not fetching. A torrent the owner stopped is not failed for
    /// having sat there while it was stopped.
    /// </remarks>
    [Fact]
    public async Task APausedMagnetIsNotFailedByTheClock()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 17, 12, 0, 0, TimeSpan.Zero));

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, null, new ActivityJournal(clock), new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

        engine.Start();

        TorrentHandle handle = await engine.AddAsync(Request, CancellationToken.None);

        await engine.PauseAsync(handle.InfoHash, CancellationToken.None);

        clock.Advance(TimeSpan.FromHours(9));

        Assert.Equal(TorrentState.Paused, (await engine.StatusAsync(CancellationToken.None))[0].State);
    }

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-engine-" + Guid.NewGuid().ToString("n")[..8]);

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>Removing one torrent's files removes that torrent's files.</strong>
    /// It deleted the download folder — <c>Directory.Delete(folder, recursive)</c>
    /// where <c>folder</c> is the folder every torrent downloads into, not the
    /// one this torrent made. So finishing one grab, or the owner cancelling
    /// one download, took every other download on the machine with it.
    /// </para>
    /// <para>
    /// It really happened, on 2 September 2026: the owner's download folder held
    /// two torrents' folders and three resume files, and after one grab was
    /// finished with there was one folder left and nothing else. Nothing
    /// irreplaceable went, because the others were already in the library — with
    /// three downloads in flight it would have wiped two of them mid-download.
    /// </para>
    /// <para>
    /// And what it does delete, it deletes wholly: the videos, whatever else the
    /// release shipped, the folder the torrent made for itself, and the resume
    /// and metadata files kept beside it. A cancelled download leaves nothing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RemovingOneTorrentLeavesEveryOtherDownloadAlone()
    {
        using BittorrentEngine engine = Started();

        Directory.CreateDirectory(_folder);

        string file = Path.Combine(_folder, "archive.torrent");
        File.WriteAllBytes(file, Fixture("archive-multifile.torrent"));

        TorrentHandle handle = await engine.AddAsync(
            Request with { Source = file, DownloadFolder = _folder },
            CancellationToken.None);

        // Whatever else is in the download folder: another torrent's work, and
        // the file this one was added from.
        string somebodyElse = Path.Combine(_folder, "Some.Other.Release-GRP");

        Directory.CreateDirectory(somebodyElse);
        await File.WriteAllTextAsync(Path.Combine(somebodyElse, "episode.mkv"), "not this torrent's");

        // The session has to be open for there to be anything of its own on
        // disk, and asking what it wants is what opens it.
        _ = await engine.FilesAsync(handle.InfoHash, CancellationToken.None);
        _ = await engine.StatusAsync(CancellationToken.None);

        await engine.RemoveAsync(handle.InfoHash, deleteFiles: true, CancellationToken.None);

        Assert.True(Directory.Exists(_folder), "the download folder itself was deleted.");
        Assert.True(Directory.Exists(somebodyElse), "another download's folder was deleted.");
        Assert.True(
            File.Exists(Path.Combine(somebodyElse, "episode.mkv")),
            "another download's file was deleted.");
        Assert.True(File.Exists(file), "a file in the download folder that is nobody's was deleted.");
    }

    /// <remarks>
    /// The other half, and the owner's own rule: a download that is removed with
    /// its files leaves nothing behind at all — not the videos, not the text
    /// files a release ships with, and not the folder it made for itself.
    /// </remarks>
    [Fact]
    public async Task ARemovedTorrentLeavesNothingOfItsOwnBehind()
    {
        using BittorrentEngine engine = Started();

        Directory.CreateDirectory(_folder);

        string file = Path.Combine(_folder, "archive.torrent");
        File.WriteAllBytes(file, Fixture("archive-multifile.torrent"));

        TorrentHandle handle = await engine.AddAsync(
            Request with { Source = file, DownloadFolder = _folder },
            CancellationToken.None);

        _ = await engine.FilesAsync(handle.InfoHash, CancellationToken.None);
        _ = await engine.StatusAsync(CancellationToken.None);

        IReadOnlyList<TorrentFile> mine = await engine.FilesAsync(handle.InfoHash, CancellationToken.None);

        Assert.NotEmpty(mine);

        // The folder the torrent made for itself, and something a release
        // brings along that is not a video and was never downloaded by us.
        string own = Path.Combine(_folder, mine[0].Path.Split('/')[0]);

        Directory.CreateDirectory(own);
        await File.WriteAllTextAsync(Path.Combine(own, "read.me.txt"), "shipped with the release");

        await engine.RemoveAsync(handle.InfoHash, deleteFiles: true, CancellationToken.None);

        Assert.False(Directory.Exists(own), $"{own} was left behind.");
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    private static BittorrentEngine Started()
    {
        BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, null, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

        engine.Start();

        return engine;
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent keeps the trackers it learned, across a restart.</strong>
    /// The trackers a torrent runs on are not the ones in its magnet. An indexer
    /// hands back a bare <c>magnet:?xt=urn:btih:…&amp;dn=…</c> with no
    /// <c>tr=</c> at all, and the fifty-nine this client ends up announcing to
    /// are learned afterwards — off the torrent file, off the swarm. Only the
    /// info dictionary was ever written down, so every restart handed the run
    /// whatever the magnet said, which for such a torrent is nobody.
    /// </para>
    /// <para>
    /// On 3 September 2026 that was Rings of Power S02E06 on the owner's server:
    /// twenty-one of fifty-nine trackers answering before the restart, and after
    /// it not one announce in thirty-six minutes — no error, nothing in the log,
    /// because a client with no trackers has nobody to ask and nothing to say.
    /// It ran on the DHT alone, found one peer, and took eight megabytes an hour
    /// off a release that had come down at fourteen megabytes a second.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentKeepsTheTrackersItLearnedAcrossARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-trackers-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            ResumeKeeper keeping = new(folder, TimeSpan.FromSeconds(1), TimeProvider.System);
            byte[] file = Fixture("archive-multifile.torrent");
            TorrentMetadata torrent = TorrentMetadata.Read(file);

            keeping.Remember(torrent.InfoHash, Info(file));

            // What the run before the restart wrote down: a tracker it had
            // learned, which is in no magnet anybody holds.
            keeping.Stop(
            [
                new ResumeData(torrent.InfoHash, new(torrent.PieceCount), 0, 0, [])
                {
                    Trackers = ["http://learned.invalid:6969/announce"],
                },
            ]);

            RecordingTrackers trackers = new();

            using BittorrentEngine engine = new(
                0,
                Timeout,
                Stall,
                Together,
                Seeding,
                0,
                0,
                null,
                new ActivityJournal(),
                new CapturingLogger(),
                trackers,
                new NoPeers(),
                null,
                keeping);

            engine.Start();

            // The magnet an indexer gives back: a hash, a name, and not one
            // tracker.
            await engine.AddAsync(
                new(
                    $"magnet:?xt=urn:btih:{torrent.InfoHash}&dn=whatever",
                    [],
                    folder,
                    torrent.TotalLength),
                CancellationToken.None);

            await trackers.Asked.WaitAsync(TimeSpan.FromSeconds(20));

            Assert.Contains(
                trackers.Addresses,
                address => address.Contains("learned.invalid", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <summary>Trackers that answer nothing and write down who was asked.</summary>
    /// <remarks>
    /// Which trackers an announce goes to is the whole of what is under test,
    /// and it is not on any status this engine offers. What the transport was
    /// handed is.
    /// </remarks>
    private sealed class RecordingTrackers : ITrackerTransport
    {
        private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<string> _addresses = [];
        private readonly Lock _lock = new();

        /// <summary>Completes the first time anything is really asked.</summary>
        public Task Asked => _asked.Task;

        /// <summary>Everything an announce was addressed to.</summary>
        public IReadOnlyList<string> Addresses
        {
            get
            {
                lock (_lock)
                {
                    return [.. _addresses];
                }
            }
        }

        public Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            Note(address.OriginalString);

            throw new HttpRequestException("nothing answered");
        }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
        {
            Note($"{host}:{port}");

            throw new TimeoutException($"{host}:{port} did not answer.");
        }

        private void Note(string address)
        {
            lock (_lock)
            {
                _addresses.Add(address);
            }

            _asked.TrySetResult();
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A refusal about the torrent has to reach the thing that acts on
    /// it.</strong> There are two kinds. "No peer sent its metadata within five
    /// minutes" is true of one evening and the release is worth asking for
    /// again; "there is no video file in it" is true of that torrent for ever
    /// and nothing will ever put one there. `Transfers` blacklists on exactly
    /// that difference — for ever, or for six hours.
    /// </para>
    /// <para>
    /// The engine knew which it was and never said. <c>Held.ErrorIsTheRelease</c>
    /// was set where the refusal is made and was never passed into the status
    /// the pipeline reads, so it arrived false every time and **no torrent has
    /// ever been refused for ever** — a 1.2 GB executable named after an episode
    /// came round again every six hours, for as long as the plugin ran.
    /// </para>
    /// <para>
    /// The fixture is a scanned book: eight files, not one of them video, which
    /// is what a fake release looks like from the inside.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentRefusedForItsOwnContentsSaysThatIsAboutTheRelease()
    {
        byte[] file = Fixture("archive-multifile.torrent");
        TorrentMetadata torrent = TorrentMetadata.Read(file);
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-refused-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, "book.torrent");
            File.WriteAllBytes(path, file);

            using BittorrentEngine engine = new(
                0,
                Timeout,
                Stall,
                Together,
                Seeding,
                0,
                0,
                null,
                new ActivityJournal(),
                new CapturingLogger(),
                new SilentTrackers(),
                new NoPeers(),
                null,
                null);

            engine.Start();

            await engine.AddAsync(new(path, [], folder, torrent.TotalLength), CancellationToken.None);

            TorrentStatus status = (await engine.StatusAsync(CancellationToken.None))[0];

            Assert.Equal(TorrentState.Error, status.State);
            Assert.Contains("no video file", status.Error ?? string.Empty, StringComparison.OrdinalIgnoreCase);

            // And this is the half that never arrived: the pipeline reads it to
            // decide between a refusal for ever and one for six hours.
            Assert.True(
                status.ErrorIsTheRelease,
                "the engine refused the torrent for its own contents and told the pipeline it was about tonight.");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent the client is no longer holding still has files on
    /// disk.</strong> <c>RemoveAsync</c> began by taking the torrent out of the
    /// table and returning if it was not there — so a removal asked for after a
    /// restart, before the plugin had handed the torrent back, deleted nothing
    /// at all and said nothing about it. The caller went on to mark the grab
    /// done.
    /// </para>
    /// <para>
    /// Measured on the owner's server, 5 September 2026: 9.4 GB in
    /// <c>D:\torrent-downloads</c> that no grab answered for — a season pack of
    /// 8.6 GB whose grab row was gone, and 594 MB belonging to a grab that had
    /// been marked done and encoded days earlier. The owner's rule is that a
    /// cancelled download leaves nothing behind, and this is the hole it was
    /// leaving through.
    /// </para>
    /// <para>
    /// The metadata is kept beside the download precisely so a torrent's own
    /// files can be named without the client holding it, which is what makes
    /// this deletable rather than a guess at a folder.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentTheClientNoLongerHoldsStillHasItsFilesDeleted()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-orphan-" + Guid.NewGuid().ToString("n")[..8]);

        try
        {
            Directory.CreateDirectory(folder);

            ResumeKeeper keeping = new(folder, TimeSpan.FromSeconds(1), TimeProvider.System);
            byte[] file = Fixture("archive-multifile.torrent");
            TorrentMetadata torrent = TorrentMetadata.Read(file);

            keeping.Remember(torrent.InfoHash, Info(file));

            // What the download left behind: the torrent's own folder, under
            // its own name, with one of its files in it.
            string left = Path.Combine(folder, torrent.Name);
            Directory.CreateDirectory(left);
            await File.WriteAllTextAsync(Path.Combine(left, "half-a-download"), "bytes");

            // An engine that has never been told about this torrent, which is
            // what a restart leaves.
            using BittorrentEngine engine = new(
                0,
                Timeout,
                Stall,
                Together,
                Seeding,
                0,
                0,
                null,
                new ActivityJournal(),
                new CapturingLogger(),
                new SilentTrackers(),
                new NoPeers(),
                null,
                keeping);

            engine.Start();

            await engine.RemoveAsync(torrent.InfoHash, deleteFiles: true, CancellationToken.None);

            Assert.False(
                Directory.Exists(left),
                "the client was not holding the torrent, so its files were left on the owner's disk.");

            // And the folder every torrent shares is not this one's to take.
            Assert.True(Directory.Exists(folder), "the download folder itself was deleted.");
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
