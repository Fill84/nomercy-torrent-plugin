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
        Database database = Database();
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
        Database database = Database();
        GrabRepository grabs = new(database);

        await Record(grabs, Hash, [Episode(1)]);
        await grabs.StateAsync(Hash, GrabState.Done, CancellationToken.None);

        // The same torrent, grabbed again before the duplicate was stopped.
        await Record(grabs, Hash, [Episode(1)]);

        await grabs.FailedAsync(Hash, "the swarm went quiet", DateTimeOffset.UtcNow, CancellationToken.None);

        Assert.Equal(1, await Done(database));

        // And it does not come back to life either.
        await grabs.StateAsync(Hash, GrabState.Downloading, CancellationToken.None);

        Assert.Equal(1, await Done(database));
    }

    /// <summary>How many rows are done, which no reading of the store answers.</summary>
    private static async Task<long> Done(Database database)
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

        await grabs.FailedAsync(Hash, "first reason", When, CancellationToken.None);
        await grabs.FailedAsync(Hash, "second reason", When, CancellationToken.None);

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

        Assert.Equal(1, await grabs.FailedAsync(Hash.ToLowerInvariant(), "gone", When, CancellationToken.None));
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

    private const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

    private static DateTimeOffset When => new(2026, 8, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// One database in a folder of its own, migrated once.
    /// </summary>
    /// <remarks>
    /// The same file for every repository in a test: two <c>Database</c> objects
    /// pointing at one folder are one database, which is what they are on a
    /// real server too.
    /// </remarks>
    private Database Database()
    {
        Directory.CreateDirectory(_folder);

        Database database = new(_folder);

        database.MigrateAsync(CancellationToken.None).GetAwaiter().GetResult();

        return database;
    }

    private GrabRepository Repository()
    {
        return new(Database());
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
