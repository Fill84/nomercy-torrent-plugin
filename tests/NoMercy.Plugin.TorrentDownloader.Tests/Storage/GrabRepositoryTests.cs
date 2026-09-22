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

        // All three grabbed as one season pack, and all three still missing —
        // taking an episode does not change its row, only the grab says it is
        // spoken for.
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
    /// <para>
    /// A failure and a state written by hash still never touch a done grab.
    /// Recording the torrent again is the one thing that does, and on purpose:
    /// since 11 September 2026 a done torrent recorded again is delivered again,
    /// because it is only recorded again for an episode the library does not
    /// have — <see cref="ATorrentThatWasDeliveredIsTakenOnAgainWhenItsEpisodeIsStillMissing"/>.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AGrabThatIsDoneIsNotDraggedBackByALaterFailure()
    {
        Store database = Store();
        GrabRepository grabs = new(database);

        await Record(grabs, Hash, [Episode(1)]);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

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
    /// <strong>A torrent that was delivered is delivered again when the library
    /// still misses its episode.</strong> The owner's decision of 11 September
    /// 2026. South Park S15E12 was downloaded and encoded on 1 September, and
    /// the server filed it under another episode — its title carries "1%" — so
    /// the episode stayed missing. The next run found the same release and
    /// handed it to the client, and twenty seconds later the plugin stopped it
    /// again: the grab was done, so the torrent in the client was nobody's.
    /// </para>
    /// <para>
    /// A run only takes a torrent for an episode the library does not have, so
    /// a done grab taken again is one to deliver again: open, with nothing
    /// staged and no encode against it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATorrentThatWasDeliveredIsTakenOnAgainWhenItsEpisodeIsStillMissing()
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

        await grabs.StagedAsync(Hash, [@"D:\intake\Silo.S03E01.1080p.mkv"], CancellationToken.None);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        Assert.Empty(await grabs.OpenAsync(CancellationToken.None));

        // The same release, found again ten days later because the library
        // still does not have the episode.
        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03E01 1080p WEB H264-CAKES",
            "TorrentBay",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1)],
            When.AddDays(10),
            CancellationToken.None);

        StoredDownload again = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        // A new download, not the old one's leftovers: nothing staged yet,
        // or the next tick would skip straight past staging it.
        Assert.Empty(again.StagedPaths);
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

    /// <remarks>
    /// <para>
    /// <strong>The job the server named is kept with the grab, per episode.</strong>
    /// A plugin on contract 12 learns what became of an encode only by asking
    /// <c>IPluginJobs</c> for the id <c>IPluginEncoder</c> handed back, and the
    /// case that matters is the one memory cannot hold: the plugin restarts with
    /// the grab still dispatched. Written before the plugin starts and read by
    /// both queries — the sweep reads every grab, recovery the open ones, and a
    /// column one of them did not read would have the sweep take a staged file
    /// the other knew was still being encoded.
    /// </para>
    /// <para>
    /// A pack's episodes are dispatched one after another, each with a job of
    /// its own, so a second id joins the first rather than replacing it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEncodeJobTheServerNamedIsKeptPerEpisodeAndReadBackByBothQueries()
    {
        GrabRepository grabs = Repository();

        await grabs.RecordAsync(
            Episode(1),
            "Silo",
            "Silo S03 1080p WEB H264-CAKES",
            "1337x",
            Hash,
            $"magnet:?xt=urn:btih:{Hash}",
            [Episode(1), Episode(2)],
            When,
            CancellationToken.None);

        await grabs.EncodeAsync(Hash, Episode(1), "01KZGKX2G0966V80H26EKGG5T1", CancellationToken.None);
        await grabs.EncodeAsync(Hash, Episode(2), "01KZGKX2G0966V80H26EKGG5T2", CancellationToken.None);

        StoredDownload open = Assert.Single(await grabs.OpenAsync(CancellationToken.None));
        StoredDownload every = Assert.Single(await grabs.EveryAsync(CancellationToken.None));

        foreach (StoredDownload read in new[] { open, every })
        {
            Assert.Equal("01KZGKX2G0966V80H26EKGG5T1", read.EncodeJobs[Episode(1)]);
            Assert.Equal("01KZGKX2G0966V80H26EKGG5T2", read.EncodeJobs[Episode(2)]);
        }

        // Asked for again — a refused ask retried — the later job is the one the
        // server is running, so it is the one kept.
        await grabs.EncodeAsync(Hash, Episode(1), "01KZGKX2G0966V80H26EKGG5T3", CancellationToken.None);

        StoredDownload again = Assert.Single(await grabs.OpenAsync(CancellationToken.None));

        Assert.Equal("01KZGKX2G0966V80H26EKGG5T3", again.EncodeJobs[Episode(1)]);
        Assert.Equal("01KZGKX2G0966V80H26EKGG5T2", again.EncodeJobs[Episode(2)]);
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

}
