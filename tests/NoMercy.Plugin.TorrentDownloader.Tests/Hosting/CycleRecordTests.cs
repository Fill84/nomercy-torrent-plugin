using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Writing down what a cycle decided.
/// </summary>
/// <remarks>
/// A cycle answered with a report and nothing ever wrote it anywhere, so the
/// Downloads page was empty while a torrent was running and the Skipped page
/// was empty however much had been refused. What a cycle decided is a fact
/// about an episode the moment the client has been handed something.
/// </remarks>
public class CycleRecordTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-cycle-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AGrabIsRecordedWithItsHashItsMagnetAndEveryEpisodeItCovers()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([Taken]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        StoredDownload stored = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(Hash, stored.InfoHash);
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", stored.ReleaseTitle);
        Assert.StartsWith("magnet:?xt=urn:btih:", stored.Magnet, StringComparison.Ordinal);
        Assert.Equal(2, stored.Covers.Count);
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent handed to the client is in the client once its row is written.</strong> The client
    /// takes the torrent before the run writes its grab down, and a pass in that gap finds a torrent the store
    /// does not know, stops it and keeps its files. The row then answers for a torrent nothing holds, and
    /// nothing put it back until the next run, a whole cadence later.
    /// </para>
    /// <para>
    /// So writing a handed-over decision starts a pass, and that pass finds the row without its torrent and
    /// hands it back. Here the pass in the gap has already stopped it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentStoppedBeforeItsGrabWasWrittenIsBackInTheClientOnceItIs()
    {
        GrabRepository grabs = await Repository();
        StandingEngine engine = new StandingEngine().Holding(Downloading());
        Transfers transfers = Transfers(engine, grabs);
        string incomplete = Path.Combine(_folder, "incomplete");
        string intake = Path.Combine(_folder, "intake");

        // The pass in the gap: the client has it, the store does not.
        await transfers.TickAsync(incomplete, intake, CancellationToken.None);

        Assert.Empty(await engine.StatusAsync(CancellationToken.None));

        CycleWriter writer = new(
            [Tracked],
            grabs,
            new EpisodeRepository(TestStore.In(_folder)),
            () => When,
            handedOver: ct => transfers.TickAsync(incomplete, intake, ct));

        await writer.DecidedAsync(Taken, CancellationToken.None);

        TorrentRequest back = Assert.Single(engine.Taken);

        Assert.Equal(Taken.Magnet, back.Source);
    }

    /// <summary>The torrent as the client reports it a moment after taking it.</summary>
    private static TorrentStatus Downloading()
    {
        return new(
            Hash,
            "Silo.S03E06.1080p.WEB.H264-CAKES",
            TorrentState.Downloading,
            BytesDone: 0,
            BytesTotal: 900_000_000,
            DownloadRateBytesPerSecond: 0,
            Peers: 0,
            Seeds: 0,
            Ratio: null,
            Eta: null,
            Error: null);
    }

    private static Transfers Transfers(StandingEngine engine, GrabRepository grabs)
    {
        FakeProvider server = new();
        FakeLibraryQuery query = new FakeLibraryQuery()
            .Library("01KZGKX2G0966V80H26EKGG5T1", "Television", "tv")
            .Show(41, "Silo", "01KZGKX2G0966V80H26EKGG5T1", year: 2023);

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

    /// <remarks>
    /// A decision the client was never handed is not a fact about an episode.
    /// Recording one would have the Downloads page show a row for a torrent
    /// nothing is downloading, which is the page saying something untrue.
    /// </remarks>
    [Fact]
    public async Task ADecisionNothingWasHandedIsNotRecordedAsAGrab()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null }]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <strong>Nothing counted a search at all.</strong> On the owner's own
    /// library every row still read nought attempts and a null last-search. And
    /// the queue is ordered by last-search — never searched first, then
    /// longest waiting — so with the column never written every cycle ran in
    /// the same order and whatever was at the end of it stayed there.
    /// </remarks>
    [Fact]
    public async Task ASearchThatWasReallyMadeIsCountedAgainstItsEpisode()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null, Searched = true }]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None,
            episodes);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(1, after.Attempts);
        Assert.Equal(When, after.LastSearchAt);

        // However many times searched, still missing: there is nothing left
        // for an attempt count to exhaust.
        Assert.Equal(EpisodeState.Missing, after.State);
    }

    /// <remarks>
    /// <strong>B2.</strong> Only a search counts. An episode settled by a pack
    /// taken earlier in the cycle, and one nothing could be asked about, have
    /// not been looked for — and in 0.3.4 three failed grabs exhausted an
    /// episode that had never had a search go badly, because the number going
    /// up looked like work.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeNoIndexerWasAskedAboutCostsItNoAttempt()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null, Searched = false }]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None,
            episodes);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(0, after.Attempts);
        Assert.Null(after.LastSearchAt);
    }

    /// <remarks>
    /// However many searches an episode has had, it is asked about again next
    /// run. Giving up never held — the refresh at the top of the next run
    /// always derived the episode as missing regardless — so the owner's
    /// decision of 12 September 2026 was to drop the limit outright rather
    /// than have it hold for a time.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithManyAttemptsStaysMissingRatherThanGivingUp()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both(attempts: 69);

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null, Searched = true }]),
            [Tracked with { Attempts = 69 }],
            grabs,
            When,
            CancellationToken.None,
            episodes);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(70, after.Attempts);
        Assert.Equal(EpisodeState.Missing, after.State);
    }

    /// <remarks>
    /// An episode whose release was taken is about to stop being missing at
    /// all, whatever it cost to find.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWhoseReleaseWasTakenIsNeverGivenUpOn()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both(attempts: 2);

        await CycleRecord.WriteAsync(
            new([Taken with { Searched = true }]),
            [Tracked with { Attempts = 2 }],
            grabs,
            When,
            CancellationToken.None,
            episodes);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(EpisodeState.Missing, after.State);
    }

    /// <remarks>
    /// <strong>Dry run decided everything and wrote down nothing.</strong> A
    /// cycle that found the right release for every episode left a Skipped page
    /// full of refusals and no trace of one thing it would have taken — and the
    /// owner read that as a plugin refusing everything, which is the only thing
    /// the evidence said. What it would take is the whole point of the switch.
    /// </remarks>
    [Fact]
    public async Task WhatADryRunWouldTakeIsWrittenDown()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new(
            [
                Taken with
                {
                    HandedOver = false,
                    InfoHash = null,
                    Detail = "would take it — dry run is on",
                },
            ]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        HistoryRow line = Assert.Single(
            await grabs.HistoryAsync(CancellationToken.None),
            row => row.Event == "decided");

        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", line.ReleaseTitle);
        Assert.Equal("1337x", line.Source);
        Assert.Contains("dry run", line.Detail!, StringComparison.OrdinalIgnoreCase);

        // And still no grab, because nothing was handed over.
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// An episode nobody is serving decided nothing, so there is nothing to
    /// write. A line naming no release would be the page inventing one.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithNoReleaseAtAllGetsNoDecisionLine()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([new(Episode, null, null, null, false, "nobody is serving one")]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        Assert.DoesNotContain(
            await grabs.HistoryAsync(CancellationToken.None),
            row => row.Event == "decided");
    }

    private const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    private static readonly DateTimeOffset When = new(2026, 8, 19, 9, 30, 0, TimeSpan.Zero);

    private static EpisodeKey Episode => new(41, 3, 6);

    private static TrackedEpisode Tracked =>
        new(Episode, "Silo", 2021, LibraryKind.Television, null, null, EpisodeState.Missing);

    private static EpisodeOutcome Taken =>
        new(Episode, "Silo.S03E06.1080p.WEB.H264-CAKES", "1337x", 240, true, "taken from 1337x")
        {
            InfoHash = Hash,
            Magnet = $"magnet:?xt=urn:btih:{Hash}",
            Covers = [Episode, new(41, 3, 7)],
        };

    /// <summary>
    /// Both stores over one database, with the episode already in it — a search
    /// can only be counted against a row that exists.
    /// </summary>
    private async Task<(GrabRepository Grabs, EpisodeRepository Episodes)> Both(int attempts = 0)
    {
        Store database = TestStore.In(_folder);

        await database.MigrateAsync(CancellationToken.None);

        EpisodeRepository episodes = new(database);

        await episodes.ReplaceAsync([Tracked], CancellationToken.None);

        // The attempts a row arrives with are the ones earlier cycles recorded,
        // and only a recorded search moves them - so they are put there the one
        // way anything can.
        for (int already = 0; already < attempts; already++)
        {
            await episodes.RecordSearchAsync(Episode, When.AddDays(-1), CancellationToken.None);
        }

        return (new(database), episodes);
    }

    private async Task<GrabRepository> Repository()
    {
        Store database = TestStore.In(_folder);

        await database.MigrateAsync(CancellationToken.None);

        return new(database);
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        // The pool holds the file open, so it cannot be deleted until every
        // connection this test opened has really gone.

        TemporaryFolder.Forget(_folder);
    }
}
