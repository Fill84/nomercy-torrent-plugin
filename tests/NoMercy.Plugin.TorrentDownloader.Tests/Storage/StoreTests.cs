using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Storage;

public class StoreTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <summary>The columns 018 adds, taken away so a database rolled back past it is one 018 can run on again.</summary>
    private const string Before018 =
        """
        ALTER TABLE grabs DROP COLUMN named;
        ALTER TABLE grabs DROP COLUMN replaces;
        ALTER TABLE grabs DROP COLUMN recycled_at;
        ALTER TABLE grabs DROP COLUMN encoded;
        ALTER TABLE episodes DROP COLUMN replaces_hash;
        ALTER TABLE episodes DROP COLUMN replaces_release;
        """;

    /// <remarks>
    /// Migrations run at every startup, and the plugin loads on every server
    /// start. A runner that was not idempotent would fail on the second start
    /// — or worse, run <c>001</c> again and lose the table.
    /// </remarks>
    [Fact]
    public async Task MigratingTwiceIsMigratingOnce()
    {
        await using Store database = TestStore.In(_folder);

        await database.MigrateAsync(CancellationToken.None);

        // Whatever ships, rather than a number that has to be edited every time
        // one is added — the fault this is watching for is a count that grows
        // on a second run, not a count of any particular size.
        long once = await Applied(database);

        Assert.InRange(once, 1, long.MaxValue);

        await database.MigrateAsync(CancellationToken.None);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(once, await Applied(database));
        Assert.Equal(0, await Count(database, "episodes"));
    }

    /// <remarks>
    /// Data written before a restart is still there after the migrations run
    /// again, which is the failure "idempotent" is really guarding against.
    /// </remarks>
    [Fact]
    public async Task MigratingAgainKeepsWhatWasThere()
    {
        await using Store database = TestStore.In(_folder);
        await database.MigrateAsync(CancellationToken.None);

        await database.ExecuteAsync(
            """
            INSERT INTO episodes (show_id, season, episode, show_title, library_type, state)
            VALUES (1, 1, 1, 'Silo', 'tv', 'missing');
            """,
            null,
            CancellationToken.None);

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
    [InlineData("show_settings")]
    [InlineData("library_preferences")]
    public async Task TheDocumentedSchemaIsWhatGetsCreated(string table)
    {
        await using Store database = TestStore.In(_folder);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(0, await Count(database, table));
    }

    /// <remarks>
    /// <c>S13-05</c>: the name pool is gone, because a run reads the name sources' feeds and asks their
    /// searches directly (<c>docs/specs/release-names.md</c>). <c>001</c> still creates the table, so a
    /// database of any age ends without it only because a later migration drops it.
    /// </remarks>
    [Fact]
    public async Task TheNamePoolIsGone()
    {
        await using Store database = TestStore.In(_folder);
        await database.MigrateAsync(CancellationToken.None);

        Assert.Equal(
            0L,
            (long)(await database.ScalarAsync(
                "SELECT COUNT(*) FROM sqlite_master WHERE name LIKE 'name_pool%';",
                null,
                CancellationToken.None))!);
    }

    /// <remarks>
    /// <strong>The refusals an older version wrote are taken off the disk.</strong> Nothing writes one any
    /// more (the owner's word of 16 September 2026), and their own history held 5,851 of them against 219
    /// lines of everything else. Leaving them would make the History page unreadable for ever on the one
    /// server it matters on.
    /// </remarks>
    [Fact]
    public async Task TheRefusalsAnOlderVersionWroteAreGone()
    {
        await using Store database = TestStore.In(_folder);
        await database.MigrateAsync(CancellationToken.None);

        // Two lines an older version wrote: a refusal, and a grab that is not
        // one. Written by hand because nothing writes a refusal now.
        await database.ExecuteAsync(
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ('2026-09-01T00:00:00.0000000+00:00', 'skipped', 41, 3, 6, 'Silo', 'Silo.S03E06.720p.WEB', '1337x', '720p is not 1080p.'),
                   ('2026-09-01T00:00:00.0000000+00:00', 'grabbed', 41, 3, 6, 'Silo', 'Silo.S03E06.1080p.WEB', '1337x', 'taken');
            """,
            null,
            CancellationToken.None);

        await BackTo(database, 15);

        // Going back in the count does not undo what the migrations did, so
        // every one after 015 runs a second time — and 017 adds a column the
        // first run has already added. SQLite has no ADD COLUMN IF NOT EXISTS,
        // so the state this test is pretending to be in has to lack it.
        await database.ExecuteAsync("ALTER TABLE grabs DROP COLUMN encode_jobs;", null, CancellationToken.None);

        // And the six 018 adds, for the same reason.
        await database.ExecuteAsync(Before018, null, CancellationToken.None);

        await database.MigrateAsync(CancellationToken.None);

        Dictionary<string, long> left = (await database.QueryAsync(
                "SELECT event, COUNT(*) AS lines FROM history GROUP BY event;",
                null,
                CancellationToken.None))
            .ToDictionary(row => row.Text("event"), row => row.Long("lines"));

        Assert.DoesNotContain("skipped", left.Keys);
        Assert.Equal(1L, left["grabbed"]);
    }

    /// <remarks>
    /// The data folder is the plugin's own and may not exist yet on a plugin
    /// installed this morning.
    /// </remarks>
    [Fact]
    public async Task AFolderThatIsNotThereYetIsCreated()
    {
        string missing = Path.Combine(_folder, "not", "there", "yet");

        await using (Store database = TestStore.In(missing))
        {
            await database.MigrateAsync(CancellationToken.None);
        }

        Assert.True(File.Exists(Path.Combine(missing, $"{Store.DatabaseName}.sqlite")));
    }

    /// <remarks>
    /// <para>
    /// <strong>The database is opened once, however many calls use it.</strong>
    /// Opening is where the host makes its own table and where this store sets
    /// <c>journal_mode</c> and <c>foreign_keys</c>; doing it on every call was,
    /// on the connection-per-call store this replaced, about 21,600 needless
    /// round trips a day with seventeen store methods and roughly fifteen calls
    /// in a transfers tick. It buys nothing and shows in no outcome, which is why
    /// this counts the opens.
    /// </para>
    /// <para>
    /// Still WAL, so a page reading while a run writes does not wait on the
    /// file — the host does not set it, so the store has to.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheDatabaseIsOpenedOnceHoweverManyTimesItIsUsed()
    {
        HostShapedStorage storage = new(_folder);
        await using Store database = new(_folder, () => storage);

        await database.MigrateAsync(CancellationToken.None);
        await Count(database, "episodes");
        await Count(database, "grabs");

        Assert.Equal(1, storage.Opens);
        Assert.Equal("wal", await database.ScalarAsync("PRAGMA journal_mode;", null, CancellationToken.None));
        Assert.Equal(1L, await database.ScalarAsync("PRAGMA foreign_keys;", null, CancellationToken.None));
    }

    /// <remarks>
    /// A cycle still running when the server unloads the plugin calls the store
    /// after it was disposed. Opening the database again then would leave a
    /// connection open to a file the next copy of the plugin is about to open,
    /// held by a plugin the server believes is gone.
    /// </remarks>
    [Fact]
    public async Task AStoreThatWasDisposedDoesNotOpenTheDatabaseAgain()
    {
        HostShapedStorage storage = new(_folder);
        Store database = new(_folder, () => storage);
        await database.MigrateAsync(CancellationToken.None);

        await database.DisposeAsync();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => Count(database, "episodes"));
        Assert.Equal(1, storage.Opens);
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
        await using Store database = TestStore.In(_folder);
        await database.MigrateAsync(CancellationToken.None);

        // The owner's disk, before this version ever ran: 008 has already been
        // applied — the table exists — but 009 has not, so the row it carries
        // still says what an older release wrote. Going back in the count is the
        // only way to put a fresh test database in that state, since a
        // migration this test adds nothing to.
        await BackTo(database, 8);

        // And the column 011 takes away, put back with it. Going back in the
        // count does not undo what the migrations did, so every one after 008
        // runs a second time — and 011 drops a column, which the first run has
        // already taken. SQLite has no DROP COLUMN IF EXISTS, so the state this
        // test is pretending to be in has to include it.
        await database.ExecuteAsync("ALTER TABLE grabs ADD COLUMN encode_job TEXT;", null, CancellationToken.None);

        // And the column 017 adds, taken away for the same reason in the other
        // direction: the second run adds it again, and SQLite has no ADD COLUMN
        // IF NOT EXISTS either.
        await database.ExecuteAsync("ALTER TABLE grabs DROP COLUMN encode_jobs;", null, CancellationToken.None);

        // And the six 018 adds, for the same reason.
        await database.ExecuteAsync(Before018, null, CancellationToken.None);

        // And the other way about for 015, which adds two columns: they are
        // there already, so the pretended state has to be without them.
        await database.ExecuteAsync(
            """
            ALTER TABLE show_settings DROP COLUMN english_only;
            ALTER TABLE library_preferences DROP COLUMN english_only;
            """,
            null,
            CancellationToken.None);

        await database.ExecuteAsync(
            """
            INSERT INTO episodes (show_id, season, episode, show_title, library_type, state, attempts)
            VALUES (1, 1, 1, 'Silo', 'tv', 'unavailable', 68);
            """,
            null,
            CancellationToken.None);

        // Only 009 is pending now, and it is the one this test is about.
        await database.MigrateAsync(CancellationToken.None);

        StoredRow row = Assert.Single(await database.QueryAsync(
            "SELECT state, attempts FROM episodes WHERE show_id = 1 AND season = 1 AND episode = 1;",
            null,
            CancellationToken.None));

        // Back to missing, and every attempt already spent on it kept — giving
        // up is gone, not the cost already paid finding out it was hopeless.
        Assert.Equal("missing", row.Text("state"));
        Assert.Equal(68, row.Int("attempts"));
    }

    /// <summary>How many migrations the host counts as applied.</summary>
    private static async Task<long> Applied(Store database)
    {
        return Convert.ToInt64(await database.ScalarAsync(
            $"SELECT MAX(applied) FROM {HostShapedDatabase.VersionTable};",
            null,
            CancellationToken.None));
    }

    /// <summary>Makes the host believe only the first <paramref name="migrations"/> ran.</summary>
    private static async Task BackTo(Store database, int migrations)
    {
        await database.ExecuteAsync(
            $"DELETE FROM {HostShapedDatabase.VersionTable} WHERE applied > $kept;",
            new Dictionary<string, object?> { ["$kept"] = migrations },
            CancellationToken.None);
    }

    private static async Task<long> Count(Store database, string table)
    {
        // The table name is from this test's own list, never from input.
        return Convert.ToInt64(await database.ScalarAsync($"SELECT COUNT(*) FROM {table};", null, CancellationToken.None));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }
}
