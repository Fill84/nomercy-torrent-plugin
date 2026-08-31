using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// What has been grabbed, against a real database.
/// </summary>
/// <remarks>
/// A real SQLite file in a temporary folder, through the real migration. A fake
/// repository would agree with whatever this code did; the schema is the thing
/// being asserted against, and it is in <c>001-initial.sql</c>.
/// </remarks>
public class GrabRepositoryTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-grabs-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The release, the hash, the source and every episode it covers. The
    /// magnet goes with it because that is what a torrent the client has
    /// forgotten is re-added from.
    /// </remarks>
    [Fact]
    public async Task AGrabRecordsTheReleaseTheHashTheSourceAndEveryEpisodeItCovers()
    {
        GrabRepository grabs = Repository();

        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03 COMPLETE 1080p",
            "LimeTorrents",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1), Episode(2), Episode(3)],
            When,
            CancellationToken.None);

        StoredDownload stored = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal(Hash, stored.InfoHash);
        Assert.Equal("Silo S03 COMPLETE 1080p", stored.ReleaseTitle);
        Assert.Equal(GrabState.Grabbed, stored.State);
        Assert.StartsWith("magnet:?xt=urn:btih:", stored.Magnet, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Recovery reads what is open. Anything done with — staged, or failed and
    /// blacklisted — is not something to re-add on the next restart.
    /// </remarks>
    [Fact]
    public async Task WhatIsFinishedWithIsNotOpen()
    {
        GrabRepository grabs = Repository();

        await Record(grabs, Hash, [Episode(1)]);

        Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        await grabs.StateAsync(Hash, GrabState.Downloading, CancellationToken.None);

        Assert.Equal(GrabState.Downloading, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// A failed download blacklists <em>the hash</em> and puts every episode it
    /// covered back to missing. Both halves, together: blacklisting without
    /// returning the episodes leaves them looking grabbed for ever, and
    /// returning them without blacklisting has the next search choose the same
    /// release and fail the same way.
    /// </para>
    /// <para>
    /// This is where a metadata timeout (`S5-07`) and a stall (`S5-12`) both
    /// arrive, which is why it needed the grab: nothing else knows which
    /// episodes a hash was fetched for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFailedDownloadBlacklistsItsHashAndReturnsEveryEpisodeItCoveredToMissing()
    {
        Store database = Store();
        GrabRepository grabs = new(database);
        EpisodeRepository episodes = new(database);

        await episodes.ReplaceAsync(
            [Tracked(1), Tracked(2), Tracked(3)],
            CancellationToken.None);

        // All three grabbed as one season pack, and all three unavailable — as
        // they would be while it was downloading.
        await episodes.MarkUnavailableAsync(Episode(1), CancellationToken.None);
        await episodes.MarkUnavailableAsync(Episode(2), CancellationToken.None);
        await episodes.MarkUnavailableAsync(Episode(3), CancellationToken.None);

        await Record(grabs, Hash, [Episode(1), Episode(2), Episode(3)]);

        int returned = await grabs.FailedAsync(
            Hash,
            "No peer sent its metadata within 5 minutes.",
            When,
            until: null,
            CancellationToken.None);

        Assert.Equal(3, returned);

        Assert.All(
            await episodes.AllAsync(CancellationToken.None),
            one => Assert.Equal(EpisodeState.Missing, one.State));

        // The hash is refused from now on, with the reason the client gave.
        Assert.Contains(Hash, await grabs.BlacklistedAsync(CancellationToken.None));

        // And it is not open any more, so recovery will not re-add the very
        // thing that just failed.
        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));
    }

    /// <remarks>
    /// <para>
    /// <strong>A grab that is done is finished with.</strong> State is written
    /// by info hash, and until 23 August 2026 every cycle recorded a fresh grab
    /// for an episode it was already downloading — so one release could have
    /// four rows under one hash. When a later one failed, it dragged the
    /// finished ones back with it: the episode went to missing and was searched
    /// for again, though its file was already staged into the library.
    /// </para>
    /// <para>
    /// It is what took the owner's finished grabs from twenty-three to eleven
    /// overnight.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrabThatIsDoneIsNotDraggedBackByALaterFailure()
    {
        Store database = Store();
        GrabRepository grabs = new(database);

        await Record(grabs, Hash, [Episode(1)]);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        // The same torrent, grabbed again before the duplicate was stopped.
        await Record(grabs, Hash, [Episode(1)]);

        await grabs.FailedAsync(Hash, "the swarm went quiet", DateTimeOffset.UtcNow, null, CancellationToken.None);

        Assert.Equal(1, await Done(database));

        // And it does not come back to life either.
        await grabs.StateAsync(Hash, GrabState.Downloading, CancellationToken.None);

        Assert.Equal(1, await Done(database));
    }

    /// <summary>How many rows are done, which no reading of the store answers.</summary>
    private static async Task<long> Done(Store database)
    {
        await using SqliteConnection connection = await database.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT count(*) FROM grabs WHERE state = 'done';";

        return (long)(await command.ExecuteScalarAsync(CancellationToken.None))!;
    }

    /// <remarks>
    /// The same hash failing twice is one blacklist row with the newer reason,
    /// not two rows and not a crash on the primary key.
    /// </remarks>
    [Fact]
    public async Task AHashThatFailsTwiceIsBlacklistedOnce()
    {
        GrabRepository grabs = Repository();

        await Record(grabs, Hash, [Episode(1)]);

        await grabs.FailedAsync(Hash, "first reason", When, null, CancellationToken.None);
        await grabs.FailedAsync(Hash, "second reason", When, null, CancellationToken.None);

        Assert.Single(await grabs.BlacklistedAsync(CancellationToken.None));
    }

    /// <remarks>
    /// A hash is written upper case here and may arrive from the wire in any
    /// case at all. Matching it exactly would leave the grab open and the
    /// episode unavailable for ever.
    /// </remarks>
    [Fact]
    public async Task AHashIsFoundWhateverCaseItArrivesIn()
    {
        GrabRepository grabs = Repository();

        await Record(grabs, Hash.ToLowerInvariant(), [Episode(1)]);

        await grabs.StateAsync(Hash, GrabState.Downloading, CancellationToken.None);

        Assert.Equal(GrabState.Downloading, Assert.Single(await grabs.OpenAsync(CancellationToken.None)).State);

        Assert.Equal(1, await grabs.FailedAsync(Hash.ToLowerInvariant(), "gone", When, null, CancellationToken.None));
    }

    /// <remarks>
    /// The history is what the owner reads to answer "what happened to that
    /// episode". A grab that reached the encoder and one that stopped at the
    /// intake folder look identical from outside without this line.
    /// </remarks>
    [Fact]
    public async Task ADispatchedEncodeIsRecordedInHistory()
    {
        GrabRepository grabs = Repository();

        await grabs.DispatchedAsync(
            Episode(6),
            "Silo",
            "Silo S03E06 1080p",
            "library-tv",
            When,
            CancellationToken.None);

        HistoryRow line = Assert.Single(await grabs.HistoryAsync(CancellationToken.None));

        Assert.Equal("dispatched", line.Event);
        Assert.Contains("library-tv", line.Detail!, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        // The pool holds the file open, so it cannot be deleted until every
        // connection this test made is really gone.

        TemporaryFolder.Forget(_folder);

        GC.SuppressFinalize(this);
    }

    /// <remarks>
    /// <para>
    /// <strong>One torrent is one grab, and the schema is what says so.</strong>
    /// A cycle records a grab for each episode it decided, so anything that
    /// decides the same episode twice in one pass — a show reached through two
    /// libraries, two cadences arriving together — writes the same info hash
    /// twice. Nothing in the table stopped it: the index on the hash was not
    /// unique.
    /// </para>
    /// <para>
    /// It was cleaned up rather than prevented: once by a migration, and again
    /// by the maintenance cadence at every start. Between two of those the
    /// Downloads page showed each release twice, every step that walked grabs
    /// walked both, and a failure had two rows to put back. On 25 August 2026
    /// three duplicates were cleared at a start and three more were on the page
    /// the same evening.
    /// </para>
    /// <para>
    /// The oldest row wins, which is the rule the migration already chose: its
    /// <c>grabbed_at</c> is when the torrent was really taken on, and the
    /// covers it carries are the whole of what that release answers for.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task OneTorrentIsOneGrabHoweverOftenItIsRecorded()
    {
        GrabRepository grabs = Repository();

        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03E01 1080p WEB H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1)],
            When,
            CancellationToken.None);

        // The same torrent again, as a second pass of the same cycle records
        // it: a different release title and a later time, so that the row that
        // survives can be told apart from the row that does not.
        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03E01 2160p WEB H265-OTHER",
            "LimeTorrents",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1)],
            When.AddMinutes(1),
            CancellationToken.None);

        StoredDownload only = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal("Silo S03E01 1080p WEB H264-CAKES", only.ReleaseTitle);
    }

    /// <remarks>
    /// <para>
    /// <strong>A torrent that failed can be taken on again.</strong> One grab
    /// per torrent is kept by a unique index on the hash, and the insert was
    /// told to do nothing about a hash already known — which is right while
    /// that grab is still open and wrong once it has failed.
    /// </para>
    /// <para>
    /// A failed row stays in the table and is hidden from the Downloads page,
    /// so the owner sees nothing grabbed, pastes the magnet by hand, and the
    /// insert is silently dropped against a row they cannot see. Two Lioness
    /// episodes were dropped for want of a peer on 26 August 2026 and could not
    /// afterwards be added by hand at all.
    /// </para>
    /// <para>
    /// So a hash already open is still left alone, and a hash that finished or
    /// failed is taken on again with whatever the new attempt carries.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentThatFailedCanBeTakenOnAgain()
    {
        GrabRepository grabs = Repository();

        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03E01 1080p WEB H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1)],
            When,
            CancellationToken.None);

        await grabs.StateAsync(Hash, GrabState.Failed, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // The owner pastes the magnet themselves, with every tracker on it.
        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03E01 1080p WEB H264-CAKES EZTV",
            "by hand",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fopen.example%3A1337",
            [Episode(1)],
            When.AddHours(1),
            CancellationToken.None);

        StoredDownload again = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal("Silo S03E01 1080p WEB H264-CAKES EZTV", again.ReleaseTitle);
        Assert.Contains("open.example", again.Magnet);
    }

    private const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

    /// <summary>A second torrent, so a clean-up cannot pass by taking everything.</summary>
    private const string Other = "A1B2C3D4E5F60718293A4B5C6D7E8F9012345678";

    private static DateTimeOffset When => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One database in a folder of its own, migrated once.
    /// </summary>
    /// <remarks>
    /// The same file for every repository in a test: two <c>Store</c> objects
    /// pointing at one folder are one database, which is what they are on a
    /// real server too.
    /// </remarks>
    private Store Store()
    {
        Directory.CreateDirectory(_folder);

        Store database = new(_folder);

        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return database;
    }

    private GrabRepository Repository()
    {
        return new(Store());
    }

    private static EpisodeKey Episode(int number)
    {
        return new(42, 3, number);
    }

    private static TrackedEpisode Tracked(int number)
    {
        return new(
            Episode(number),
            "Silo",
            2023,
            LibraryKind.Television,
            $"Episode {number}",
            new DateOnly(2026, 1, 1),
            EpisodeState.Missing);
    }

    private static async Task Record(GrabRepository grabs, string hash, IReadOnlyList<EpisodeKey> covers)
    {
        await grabs.RecordAsync(
            covers[0],
            "Silo",
            "Silo S03 COMPLETE 1080p",
            "LimeTorrents",
            hash,
            $"magnet:?xt=urn:btih:{hash}",
            covers,
            When,
            CancellationToken.None);
    }

    /// <remarks>
    /// <para>
    /// <strong>A page of refusals, never all of them.</strong> One row is
    /// written for every release every cycle considered and did not take, so
    /// the owner's history reached 66,149 lines with 65,878 of them refusals —
    /// and the Skipped page selected every one and drew every one. It took the
    /// better part of a minute to open.
    /// </para>
    /// <para>
    /// Pruning does not answer it: a fortnight of a busy library is still tens
    /// of thousands of rows. A limit does.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheSkippedPageReadsOnePageAndSaysHowManyThereAre()
    {
        GrabRepository grabs = Repository();

        for (int number = 1; number <= 25; number++)
        {
            await grabs.RecordSkippedAsync(
                Episode(number),
                "Silo",
                $"Silo S03E{number:00} 720p WEB",
                "1337x",
                "720p is below the floor",
                When,
                CancellationToken.None);
        }

        SkippedPage first = await grabs.SkippedAsync(1, 10, CancellationToken.None);

        Assert.Equal(10, first.Rows.Count);
        Assert.Equal(25, first.Total);
        Assert.Equal(3, first.Pages);
        Assert.False(first.HasPrevious);
        Assert.True(first.HasNext);

        // Newest first, so the first page opens on the most recent refusal.
        Assert.Equal("Silo S03E25 720p WEB", first.Rows[0].Title);

        SkippedPage last = await grabs.SkippedAsync(3, 10, CancellationToken.None);

        Assert.Equal(5, last.Rows.Count);
        Assert.True(last.HasPrevious);
        Assert.False(last.HasNext);
        Assert.Equal("Silo S03E01 720p WEB", last.Rows[^1].Title);

        // No page overlaps another, or the owner reads the same refusal twice
        // and never sees one of the others.
        SkippedPage second = await grabs.SkippedAsync(2, 10, CancellationToken.None);

        Assert.Empty(first.Rows.Select(one => one.Title).Intersect(second.Rows.Select(one => one.Title)));
    }

}
