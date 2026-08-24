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
