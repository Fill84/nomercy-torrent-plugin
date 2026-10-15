using Microsoft.Data.Sqlite;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// A database exactly as 0.8.1 leaves one: <c>torrent-downloader.db</c>, in WAL
/// mode, every migration of <c>Storage/Migrations</c> run in number order with
/// <c>PRAGMA user_version</c> set to its number inside the same transaction, and
/// one known row in every table.
/// </summary>
/// <remarks>
/// Written here with SQLite directly rather than through the plugin, because the
/// plugin no longer writes this file: it is what every owner's server holds on
/// the day they update, and the carry-over has to read it as it is.
/// </remarks>
public static class OldDatabase
{
    public const string FileName = "torrent-downloader.db";

    public const string Hash = "0123456789ABCDEF0123456789ABCDEF01234567";

    /// <summary>The migrations 0.8.1 ships, from the repository's own files.</summary>
    public static int Migrations => MigrationFiles().Count;

    /// <summary>Writes it into <paramref name="folder"/> and closes it, as a server that stopped cleanly does.</summary>
    public static void Write(string folder)
    {
        Directory.CreateDirectory(folder);

        using SqliteConnection connection = Open(Path.Combine(folder, FileName));

        Shape(connection);
        Fill(connection);
    }

    /// <summary>
    /// Writes it into <paramref name="folder"/> as a server that died leaves it:
    /// the file as of the last checkpoint, and the rows written since only in
    /// <c>torrent-downloader.db-wal</c>.
    /// </summary>
    public static void WriteLeavingTheLog(string folder)
    {
        Directory.CreateDirectory(folder);
        string staging = Path.Combine(folder, "staging");
        Directory.CreateDirectory(staging);
        string path = Path.Combine(staging, FileName);

        using (SqliteConnection connection = Open(path))
        {
            Shape(connection);

            // Everything so far into the file, and nothing after it: the rows
            // below stay in the log for as long as this connection is open.
            Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            Execute(connection, "PRAGMA wal_autocheckpoint=0;");
            Fill(connection);

            // Copied while it is still open, which is the state a crash leaves.
            // SQLite holds the files for writing, so they are read sharing that.
            Copy(path, Path.Combine(folder, FileName));
            Copy(path + "-wal", Path.Combine(folder, FileName + "-wal"));
        }

        TemporaryFolder.Forget(staging);
    }

    private static SqliteConnection Open(string path)
    {
        // Not pooled, so closing it lets go of the file: the carry-over moves it.
        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");

        return connection;
    }

    private static void Shape(SqliteConnection connection)
    {
        foreach ((long number, string sql) in MigrationFiles())
        {
            using SqliteTransaction transaction = connection.BeginTransaction();
            Execute(connection, sql, transaction);
            Execute(connection, $"PRAGMA user_version={number};", transaction);
            transaction.Commit();
        }
    }

    private static void Fill(SqliteConnection connection)
    {
        Execute(
            connection,
            $"""
            INSERT INTO episodes (show_id, season, episode, show_title, show_year, library_type, absolute,
                                  episode_title, air_date, state, attempts, last_search_at)
            VALUES (41, 3, 6, 'Silo', 2023, 'tv', NULL, 'The Book', '2026-10-02', 'missing', 4,
                    '2026-10-14T21:00:00.0000000+00:00');

            INSERT INTO grabs (show_id, season, episode, release_title, info_hash, source, magnet, grabbed_at,
                               state, covers, staged_path, folder, encode_jobs)
            VALUES (41, 3, 6, 'Silo.S03E06.1080p.WEB.h264-ETHEL', '{Hash}', '1337x',
                    'magnet:?xt=urn:btih:{Hash}', '2026-10-14T21:05:00.0000000+00:00', 'staged',
                    '[[41,3,6]]', 'C:\intake\Silo.S03E06.mkv', 'Silo (2023)', '41x3x6:job-7');

            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ('2026-10-14T21:05:00.0000000+00:00', 'grabbed', 41, 3, 6, 'Silo',
                    'Silo.S03E06.1080p.WEB.h264-ETHEL', '1337x', 'taken');

            INSERT INTO blacklist (key, reason, at, until)
            VALUES ('FEDCBA9876543210FEDCBA9876543210FEDCBA98', 'no peers', '2026-10-13T10:00:00.0000000+00:00', NULL);

            INSERT INTO runs (started_at, ended_at, outcome)
            VALUES ('2026-10-14T21:00:00.0000000+00:00', '2026-10-14T21:09:00.0000000+00:00', 'Finished');

            INSERT INTO cadences (name, last_finished_at) VALUES ('cycle', '2026-10-14T21:09:00.0000000+00:00');

            INSERT INTO source_reports (name, at, rows, refusal, duration_ms)
            VALUES ('PreDB', '2026-10-14T21:01:00.0000000+00:00', 12, NULL, 840);

            INSERT INTO show_settings (show_id, switched_on, quality, codec, specials, wishes, musts, forbidden, english_only)
            VALUES (41, 1, '1080p', 'x265', 0, '["REMUX"]', '[]', '["CAM"]', 1);

            INSERT INTO library_preferences (library_id, quality, codec, specials, wishes, musts, forbidden, english_only)
            VALUES ('01J0LIBRARY0000000000000000', '2160p', 'any', 1, '[]', '["WEB"]', '[]', 0);
            """);
    }

    private static List<(long Number, string Sql)> MigrationFiles()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        string folder = Path.Combine(directory!.FullName, "src", "NoMercy.Plugin.TorrentDownloader", "Storage", "Migrations");

        return [.. Directory.EnumerateFiles(folder, "*.sql")
            .Select(path => (Number: long.Parse(new string([.. Path.GetFileName(path).TakeWhile(char.IsAsciiDigit)])), Sql: File.ReadAllText(path)))
            .OrderBy(migration => migration.Number)];
    }

    private static void Execute(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private static void Copy(string from, string to)
    {
        using FileStream source = new(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using FileStream target = new(to, FileMode.CreateNew, FileAccess.Write);
        source.CopyTo(target);
    }
}
