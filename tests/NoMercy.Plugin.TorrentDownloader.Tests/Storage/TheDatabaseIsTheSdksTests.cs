using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

/// <summary>
/// The plugin's database is the one the SDK opens, and an owner's 0.8.1
/// database comes along whole (S16-04).
/// </summary>
/// <remarks>
/// <para>
/// Media-server #476: the marketplace refuses <c>Microsoft.Data.Sqlite</c> and
/// <c>SQLitePCLRaw</c> shipped in the plugin, so the plugin opens its database
/// through <c>IPluginStorage.OpenDatabaseAsync</c>. The host opens
/// <c>torrent-downloader.sqlite</c> and counts migrations in a table of its own;
/// 0.8.1 kept <c>torrent-downloader.db</c> and counted in <c>user_version</c>.
/// </para>
/// <para>
/// Every owner's server holds months of grabs, refusals and history in that
/// file, and none of it can be rebuilt from the library. Losing it on an update
/// is the one thing this slice must not do.
/// </para>
/// </remarks>
public sealed class TheDatabaseIsTheSdksTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    [Fact]
    public async Task EveryRowOfA081DatabaseReadsBackTheSame()
    {
        OldDatabase.Write(_folder);

        await using Store store = TestStore.In(_folder);
        await store.MigrateAsync(CancellationToken.None);

        await RowsAreThere(store);
    }

    [Fact]
    public async Task RowsOnlyInTheWriteAheadLogComeAlongToo()
    {
        // A server that died leaves the latest rows in the -wal file. Moving the
        // database without its log keeps a file that never heard of them.
        OldDatabase.WriteLeavingTheLog(_folder);

        await using Store store = TestStore.In(_folder);
        await store.MigrateAsync(CancellationToken.None);

        await RowsAreThere(store);
    }

    [Fact]
    public async Task TheServersCountStartsWhere081Stopped()
    {
        OldDatabase.Write(_folder);

        await using (Store store = TestStore.In(_folder))
        {
            await store.MigrateAsync(CancellationToken.None);
        }

        // Counted as applied, so the host never runs 001-initial over tables
        // that are already there — which would fail the plugin's first open.
        Assert.Equal(OldDatabase.Migrations, await Counted());
        Assert.False(File.Exists(Path.Combine(_folder, OldDatabase.FileName)));

        // And opening it again changes nothing and refuses nothing.
        await using Store again = TestStore.In(_folder);
        await again.MigrateAsync(CancellationToken.None);
        Assert.Equal(OldDatabase.Migrations, await Counted());
        Assert.Single(await new EpisodeRepository(again).AllAsync(CancellationToken.None));
    }

    [Fact]
    public async Task AFreshInstallGetsEveryMigration()
    {
        await using Store store = TestStore.In(_folder);
        await store.MigrateAsync(CancellationToken.None);

        Assert.Equal(OldDatabase.Migrations, await Counted());
        Assert.Empty(await new EpisodeRepository(store).AllAsync(CancellationToken.None));
    }

    [Fact]
    public void TheShippedMigrationsAreNumberedOneToTheLastWithNoGap()
    {
        // The host counts migrations by their place in the list and 0.8.1 by the
        // number in the file name. The carry-over starts the host's count at
        // 0.8.1's number, which is the same place only while the numbers run
        // 1, 2, 3 … with none missing.
        IReadOnlyList<long> numbers = MigrationNumbers();

        Assert.Equal(Enumerable.Range(1, numbers.Count).Select(number => (long)number), numbers);
    }

    [Fact]
    public async Task AFileThatCannotBeMovedIsLeftWhereItIsAndSaysWhy()
    {
        // Windows refuses to move a file another handle holds, which is what an
        // update while the server runs leaves: the old copy's pooled connection
        // still has it. Linux moves an open file, so there is nothing to refuse.
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        OldDatabase.Write(_folder);
        string old = Path.Combine(_folder, OldDatabase.FileName);

        await using (FileStream held = new(old, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            await using Store store = TestStore.In(_folder);

            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
                () => store.MigrateAsync(CancellationToken.None));

            Assert.Contains("restart the server", refused.Message, StringComparison.Ordinal);
        }

        // Nothing made in its place: an empty database opened beside it would be
        // migrated, and the plugin would start again from nothing.
        Assert.True(File.Exists(old));
        Assert.False(File.Exists(Path.Combine(_folder, $"{Store.DatabaseName}.sqlite")));

        // And once nothing holds it, the next open carries it over.
        await using Store later = TestStore.In(_folder);
        await later.MigrateAsync(CancellationToken.None);
        await RowsAreThere(later);
    }

    [Fact]
    public async Task ADatabaseTheServerOpensSomewhereElseIsRefused()
    {
        OldDatabase.Write(_folder);
        string elsewhere = Path.Combine(_folder, "elsewhere");

        await using Store store = new(_folder, () => new HostShapedStorage(elsewhere));

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.MigrateAsync(CancellationToken.None));

        Assert.Contains(elsewhere, refused.Message, StringComparison.OrdinalIgnoreCase);

        // Not migrated into: a fresh database there would have been the plugin's
        // from now on, empty.
        await using SqliteConnection wrong = Raw(Path.Combine(elsewhere, $"{Store.DatabaseName}.sqlite"));
        Assert.Equal(0L, Scalar(wrong, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'episodes';"));
    }

    [Fact]
    public async Task AReadWaitsForATransactionInProgress()
    {
        // The host gives one connection and no lock. Without the store's own
        // gate, a page reading while a run writes reads the run's half-done work
        // on the same connection.
        await using Store store = TestStore.In(_folder);
        await store.MigrateAsync(CancellationToken.None);

        TaskCompletionSource inside = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource carryOn = new(TaskCreationOptions.RunContinuationsAsynchronously);

        Task writing = store.TransactionAsync(
            async session =>
            {
                await session.ExecuteAsync(
                    "INSERT INTO cadences (name, last_finished_at) VALUES ('cycle', '2026-10-15T10:00:00.0000000+00:00');",
                    null,
                    CancellationToken.None);
                inside.SetResult();
                await carryOn.Task;
            },
            CancellationToken.None);

        await inside.Task;
        Task<IReadOnlyDictionary<string, DateTimeOffset>> reading =
            new CadenceRepository(store).LastFinishedAsync(CancellationToken.None);

        await Task.Delay(200);
        Assert.False(reading.IsCompleted, "A read finished while a transaction was still open.");

        carryOn.SetResult();
        await writing;

        Assert.True((await reading).ContainsKey("cycle"));
    }

    [Fact]
    public async Task ATransactionThatThrowsLeavesNothingOfItself()
    {
        await using Store store = TestStore.In(_folder);
        await store.MigrateAsync(CancellationToken.None);

        await Assert.ThrowsAsync<TimeoutException>(() => store.TransactionAsync(
            async session =>
            {
                await session.ExecuteAsync(
                    "INSERT INTO cadences (name, last_finished_at) VALUES ('cycle', '2026-10-15T10:00:00.0000000+00:00');",
                    null,
                    CancellationToken.None);

                throw new TimeoutException("half way");
            },
            CancellationToken.None));

        Assert.Empty(await new CadenceRepository(store).LastFinishedAsync(CancellationToken.None));

        // And the connection is not left inside it: the next write works.
        await new CadenceRepository(store).RecordFinishedAsync("cycle", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.Single(await new CadenceRepository(store).LastFinishedAsync(CancellationToken.None));
    }

    /// <summary>The row <see cref="OldDatabase"/> put in each table, read back through the plugin.</summary>
    private static async Task RowsAreThere(Store store)
    {
        CancellationToken ct = CancellationToken.None;

        TrackedEpisode episode = Assert.Single(await new EpisodeRepository(store).AllAsync(ct));
        Assert.Equal(new EpisodeKey(41, 3, 6), episode.Key);
        Assert.Equal("Silo", episode.ShowTitle);
        Assert.Equal(2023, episode.ShowYear);
        Assert.Equal("The Book", episode.EpisodeTitle);
        Assert.Equal(new DateOnly(2026, 10, 2), episode.AirDate);
        Assert.Equal(4, episode.Attempts);

        StoredDownload grab = Assert.Single(await new GrabRepository(store).EveryAsync(ct));
        Assert.Equal(OldDatabase.Hash, grab.InfoHash);
        Assert.Equal("Silo.S03E06.1080p.WEB.h264-ETHEL", grab.ReleaseTitle);
        Assert.Equal(GrabState.Staged, grab.State);
        Assert.Equal([new EpisodeKey(41, 3, 6)], grab.Covers);
        Assert.Equal([@"C:\intake\Silo.S03E06.mkv"], grab.StagedPaths);
        Assert.Equal("Silo (2023)", grab.Folder);
        Assert.Equal("job-7", grab.EncodeJobs[new EpisodeKey(41, 3, 6)]);

        HistoryRow line = Assert.Single(await new GrabRepository(store).HistoryAsync(ct));
        Assert.Equal("grabbed", line.Event);
        Assert.Equal("taken", line.Detail);

        Assert.Contains("FEDCBA9876543210FEDCBA9876543210FEDCBA98", await new GrabRepository(store).BlacklistedAsync(ct));

        LastRun? run = await new RunRepository(store).LastAsync(ct);
        Assert.Equal(RunEnd.Finished, run?.How);

        Assert.True((await new CadenceRepository(store).LastFinishedAsync(ct)).ContainsKey("cycle"));

        SourceAnswer answer = (await new SourceLedgerRepository(store).AllAsync(ct))["PreDB"];
        Assert.Equal(12, answer.Rows);
        Assert.Equal(TimeSpan.FromMilliseconds(840), answer.Duration);

        ShowSettings show = (await new ShowSettingsRepository(store).AllAsync(ct))[41];
        Assert.True(show.SwitchedOn);
        Assert.Equal("x265", show.Codec);
        Assert.Equal(["REMUX"], show.Wishes);
        Assert.True(show.EnglishOnly);

        LibraryPreferences library = await new LibraryPreferencesRepository(store).ForAsync("01J0LIBRARY0000000000000000", ct);
        Assert.Equal("2160p", library.Quality);
        Assert.True(library.Specials);
        Assert.Equal(["WEB"], library.Musts);
    }

    private async Task<long> Counted()
    {
        await using SqliteConnection connection = Raw(Path.Combine(_folder, $"{Store.DatabaseName}.sqlite"));

        return Scalar(connection, $"SELECT MAX(applied) FROM {HostShapedDatabase.VersionTable};");
    }

    private static SqliteConnection Raw(string path)
    {
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());

        connection.Open();

        return connection;
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;

        return Convert.ToInt64(command.ExecuteScalar());
    }

    private static IReadOnlyList<long> MigrationNumbers()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return [.. Directory
            .EnumerateFiles(Path.Combine(directory!.FullName, "src", "NoMercy.Plugin.TorrentDownloader", "Storage", "Migrations"), "*.sql")
            .Select(path => long.Parse(new string([.. Path.GetFileName(path).TakeWhile(char.IsAsciiDigit)])))
            .Order()];
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }
}
