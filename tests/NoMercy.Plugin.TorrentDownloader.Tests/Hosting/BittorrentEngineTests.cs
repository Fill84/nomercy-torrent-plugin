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
        using BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

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
        BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

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

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, journal, new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

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

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, journal, new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

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

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, new ActivityJournal(clock), new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

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

        using BittorrentEngine engine = new(0, TimeSpan.FromMinutes(5), Stall, Together, Seeding, 0, 0, new ActivityJournal(clock), new CapturingLogger(), new SilentTrackers(), new NoPeers(), clock);

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
        BittorrentEngine engine = new(0, Timeout, Stall, Together, Seeding, 0, 0, new ActivityJournal(), new CapturingLogger(), new SilentTrackers(), new NoPeers());

        engine.Start();

        return engine;
    }
}
