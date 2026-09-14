using System.Data;

using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

public class StoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <remarks>
    /// Migrations run at every startup, and the plugin loads on every server
    /// start. A runner that was not idempotent would fail on the second start
    /// — or worse, run <c>001</c> again and lose the table.
    /// </remarks>
    [Fact]
    public async Task MigratingTwiceIsMigratingOnce()
    {
        Store database = new(_folder);

        await database.MigrateAsync(CancellationToken.None);

        // Whatever ships, rather than a number that has to be edited every time
        // one is added — the fault this is watching for is a version that grows
        // on a second run, not a version of any particular size.
        long once = await Version(database);

        Assert.InRange(once, 1, long.MaxValue);

        await database.MigrateAsync(CancellationToken.None);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(once, await Version(database));
        Assert.Equal(0, await Count(database, "episodes"));
    }

    /// <remarks>
    /// Data written before a restart is still there after the migrations run
    /// again, which is the failure "idempotent" is really guarding against.
    /// </remarks>
    [Fact]
    public async Task MigratingAgainKeepsWhatWasThere()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        await using (SqliteConnection connection = await database.OpenAsync(CancellationToken.None))
        {
            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO episodes (show_id, season, episode, show_title, library_type, state)
                VALUES (1, 1, 1, 'Silo', 'tv', 'missing');
                """;
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(1, await Count(database, "episodes"));
    }

    /// <remarks>
    /// The whole schema, not only the table this slice uses. It is one
    /// migration, so a table missing from it is a table missing for ever after
    /// — <c>001</c> never runs again on a database that has it.
    /// </remarks>
    [Theory]
    [InlineData("episodes")]
    [InlineData("grabs")]
    [InlineData("source_reports")]
    [InlineData("blacklist")]
    [InlineData("history")]
    [InlineData("name_pool")]
    public async Task TheDocumentedSchemaIsWhatGetsCreated(string table)
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(0, await Count(database, table));
    }

    /// <remarks>
    /// The data folder is the plugin's own and may not exist yet on a plugin
    /// installed this morning.
    /// </remarks>
    [Fact]
    public async Task AFolderThatIsNotThereYetIsCreated()
    {
        string missing = Path.Combine(_folder, "not", "there", "yet");

        await new Store(missing).MigrateAsync(CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(missing, Store.FileName)));
    }

    /// <remarks>
    /// <para>
    /// <strong>The folder and the file-level pragma are done once, not on every
    /// call.</strong> <c>journal_mode</c> is a property of the database file:
    /// once set it stays set, in that file, for every connection that ever
    /// opens it. The data folder exists after the first call for the same
    /// reason — nothing takes it away.
    /// </para>
    /// <para>
    /// Doing both again on every call was about 21,600 directory creations and
    /// 21,600 round trips a day, with seventeen store methods and roughly
    /// fifteen calls in a transfers tick. It bought nothing, and nothing about
    /// it showed in an outcome, which is why this counts rather than asserts a
    /// result.
    /// </para>
    /// <para>
    /// <c>foreign_keys</c> is not here. That one is genuinely per connection —
    /// it is off again on the next one — and it stays exactly where it was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheFileIsPreparedOnceHoweverManyTimesItIsOpened()
    {
        Store database = new(_folder);

        await database.MigrateAsync(CancellationToken.None);

        await using (SqliteConnection first = await database.OpenAsync(CancellationToken.None))
        {
            Assert.Equal(ConnectionState.Open, first.State);
        }

        await using (SqliteConnection second = await database.OpenAsync(CancellationToken.None))
        {
            Assert.Equal(ConnectionState.Open, second.State);

            // Still WAL, because the file kept it. This is the whole reason
            // setting it again is waste rather than caution.
            await using SqliteCommand asking = second.CreateCommand();
            asking.CommandText = "PRAGMA journal_mode;";

            Assert.Equal("wal", Convert.ToString(await asking.ExecuteScalarAsync(CancellationToken.None)));
        }

        Assert.Equal(1, database.TimesPrepared);
    }

    /// <remarks>
    /// Migration 009. <c>Unavailable</c> never held — the refresh at the top of
    /// the next run derived the row as missing again and kept the attempt count
    /// climbing regardless, which is exactly what left the owner's Freak
    /// Brothers episodes at 67-69 attempts on 11 September 2026. The state is
    /// gone from the domain because of it, so a row already on disk that still
    /// says 'unavailable' has to become one <c>EpisodeStates.FromStored</c> can
    /// still read, without losing what it had already tried.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeGivenUpOnComesBackAsMissing()
    {
        Store database = new(_folder);
        await database.MigrateAsync(CancellationToken.None);

        await using (SqliteConnection connection = await database.OpenAsync(CancellationToken.None))
        {
            // The owner's disk, before this version ever ran: 008 has already
            // been applied — the table exists — but 009 has not, so the row it
            // carries still says what an older release wrote. Rolling the
            // version pragma back is the only way to put a fresh test database
            // in that state, since a migration this test adds nothing to.
            await using SqliteCommand rollBack = connection.CreateCommand();
            rollBack.CommandText = "PRAGMA user_version=8;";
            await rollBack.ExecuteNonQueryAsync(CancellationToken.None);

            // And the column 011 takes away, put back with it. Rolling the
            // pragma back does not undo what the migrations did, so every one
            // after 008 runs a second time - and 011 drops a column, which the
            // first run has already taken. SQLite has no DROP COLUMN IF EXISTS,
            // so the state this test is pretending to be in has to include it.
            await using SqliteCommand back = connection.CreateCommand();
            back.CommandText = "ALTER TABLE grabs ADD COLUMN encode_job TEXT;";
            await back.ExecuteNonQueryAsync(CancellationToken.None);

            await using SqliteCommand insert = connection.CreateCommand();
            insert.CommandText =
                """
                INSERT INTO episodes (show_id, season, episode, show_title, library_type, state, attempts)
                VALUES (1, 1, 1, 'Silo', 'tv', 'unavailable', 68);
                """;
            await insert.ExecuteNonQueryAsync(CancellationToken.None);
        }

        // Only 009 is pending now, and it is the one this test is about.
        await database.MigrateAsync(CancellationToken.None);

        await using SqliteConnection read = await database.OpenAsync(CancellationToken.None);
        await using SqliteCommand select = read.CreateCommand();
        select.CommandText =
            "SELECT state, attempts FROM episodes WHERE show_id = 1 AND season = 1 AND episode = 1;";

        await using SqliteDataReader row = await select.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await row.ReadAsync(CancellationToken.None));

        // Back to missing, and every attempt already spent on it kept — giving
        // up is gone, not the cost already paid finding out it was hopeless.
        Assert.Equal("missing", row.GetString(0));
        Assert.Equal(68, row.GetInt32(1));
    }

    private static async Task<long> Version(Store database)
    {
        await using SqliteConnection connection = await database.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    private static async Task<long> Count(Store database, string table)
    {
        await using SqliteConnection connection = await database.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        // The table name is from this test's own list, never from input.
        command.CommandText = $"SELECT COUNT(*) FROM {table};";

        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }

    public void Dispose()
    {

        TemporaryFolder.Forget(_folder);
    }
}
