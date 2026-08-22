using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
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
            new([Taken], []),
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
    /// A decision the client was never handed is not a fact about an episode.
    /// Recording one would have the Downloads page show a row for a torrent
    /// nothing is downloading, which is the page saying something untrue.
    /// </remarks>
    [Fact]
    public async Task ADecisionNothingWasHandedIsNotRecordedAsAGrab()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null }], []),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// Every refusal, with the reason it was refused for. The Skipped page is
    /// opened the morning after an episode did not arrive, and a list held for
    /// the cycle would be gone by then.
    /// </remarks>
    [Fact]
    public async Task EveryRefusalIsRecordedWithItsReason()
    {
        GrabRepository grabs = await Repository();

        await CycleRecord.WriteAsync(
            new([], [new(Episode, "Silo.S03E06.720p.WEB-DL", "1337x", "720p is below the 1080p rung")]),
            [Tracked],
            grabs,
            When,
            CancellationToken.None);

        SkippedRelease refused = Assert.Single(await grabs.SkippedAsync(CancellationToken.None));

        Assert.Equal("Silo.S03E06.720p.WEB-DL", refused.Title);
        Assert.Equal("1337x", refused.Source);
        Assert.Contains("1080p rung", refused.Reason, StringComparison.Ordinal);
    }


    /// <remarks>
    /// <strong>Nothing counted a search at all.</strong> On the owner's own
    /// library every row still read nought attempts and a null last-search,
    /// which cost two things. <c>MaxSearchAttempts</c> decided nothing, so no
    /// episode ever reached <em>given up for now</em> and the Queue page's
    /// third list could not fill. And the queue is ordered by last-search —
    /// never searched first, then longest waiting — so with the column never
    /// written every cycle ran in the same order and whatever was at the end of
    /// it stayed there.
    /// </remarks>
    [Fact]
    public async Task ASearchThatWasReallyMadeIsCountedAgainstItsEpisode()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both();

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null, Searched = true }], []),
            [Tracked],
            grabs,
            When,
            CancellationToken.None,
            episodes,
            maxAttempts: 3);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(1, after.Attempts);
        Assert.Equal(When, after.LastSearchAt);

        // One of three, so it is still being looked for.
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
            new([Taken with { HandedOver = false, InfoHash = null, Searched = false }], []),
            [Tracked],
            grabs,
            When,
            CancellationToken.None,
            episodes,
            maxAttempts: 3);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(0, after.Attempts);
        Assert.Null(after.LastSearchAt);
    }

    /// <remarks>
    /// The last of the owner's attempts gives up on the episode for now. Not
    /// for good: the next maintenance pass re-derives every state from the
    /// library, so a release that appears next week puts it back to missing —
    /// which is <strong>B1</strong>, and the reason this is written here rather
    /// than in the refresh.
    /// </remarks>
    [Fact]
    public async Task TheLastAttemptGivesUpOnTheEpisodeForNow()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both(attempts: 2);

        await CycleRecord.WriteAsync(
            new([Taken with { HandedOver = false, InfoHash = null, Searched = true }], []),
            [Tracked with { Attempts = 2 }],
            grabs,
            When,
            CancellationToken.None,
            episodes,
            maxAttempts: 3);

        TrackedEpisode after = Assert.Single(await episodes.AllAsync(CancellationToken.None));

        Assert.Equal(3, after.Attempts);
        Assert.Equal(EpisodeState.Unavailable, after.State);
    }

    /// <remarks>
    /// An episode whose release was taken is not given up on, whatever it cost
    /// to find. It is about to stop being missing at all.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWhoseReleaseWasTakenIsNeverGivenUpOn()
    {
        (GrabRepository grabs, EpisodeRepository episodes) = await Both(attempts: 2);

        await CycleRecord.WriteAsync(
            new([Taken with { Searched = true }], []),
            [Tracked with { Attempts = 2 }],
            grabs,
            When,
            CancellationToken.None,
            episodes,
            maxAttempts: 3);

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
                ],
                []),
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
            new([new(Episode, null, null, null, false, "nobody is serving one")], []),
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
        Database database = new(_folder);

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
        Database database = new(_folder);

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
