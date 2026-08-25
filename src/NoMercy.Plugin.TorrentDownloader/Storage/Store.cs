using System.Reflection;
using Microsoft.Data.Sqlite;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// The plugin's own SQLite database, and the migrations that shape it.
/// </summary>
/// <remarks>
/// A JSON file was 0.3.4's store and is the wrong shape: every write rewrites
/// everything, two cadences writing at once lose each other's work, and asking
/// what is still missing means loading all of it.
/// </remarks>
public sealed class Store
{
    /// <summary>The file, inside the plugin's own data folder.</summary>
    public const string FileName = "torrent-downloader.db";

    private readonly string _connectionString;

    private readonly string _dataFolderPath;

    /// <summary>
    /// Whether this store has done the file-level setting-up yet.
    /// </summary>
    /// <remarks>
    /// Per store, which is per database file, and not static: another store is
    /// another file, and that file's <c>journal_mode</c> has not been set by
    /// anything this one did. The plugin has one store, so once per store is
    /// once per run.
    /// </remarks>
    private bool _prepared;

    /// <remarks>
    /// Builds a connection string and touches nothing. The plugin constructs
    /// this while the server is still coming up, and <c>Initialize</c> is not
    /// allowed to do I/O — the folder is made when the database is first
    /// actually opened.
    /// </remarks>
    public Store(string dataFolderPath)
    {
        _dataFolderPath = dataFolderPath;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataFolderPath, FileName),
            // The file is created on first use rather than demanded to exist:
            // a plugin installed this morning has no database yet.
            Mode = SqliteOpenMode.ReadWriteCreate,
            // The maintenance pass writes while pages read. Without this a
            // reader and a writer lock each other out and the page that was
            // showing what is happening is the one that fails.
            Cache = SqliteCacheMode.Shared,
        }.ToString();
    }

    /// <summary>
    /// How many times the file-level setting-up has been done.
    /// </summary>
    /// <remarks>
    /// Public because it is the only way to see this from outside: doing the
    /// same file-level work on every call costs a round trip and changes
    /// nothing, and nothing about it shows in an outcome. With seventeen store
    /// methods and roughly fifteen calls in a transfers tick, it was about
    /// 21,600 needless round trips a day.
    /// </remarks>
    public int TimesPrepared { get; private set; }

    /// <summary>An open connection, with the pragmas this store depends on set.</summary>
    public async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        // Once per file, not once per call. Two callers can both find it
        // undone and both do it, and that costs one extra round trip and
        // nothing else: making a folder that exists and setting a journal mode
        // that is already set are both writes that change nothing.
        bool first = !_prepared;

        if (first)
        {
            // Here rather than in the constructor: the data folder of a plugin
            // installed this morning does not exist yet, and making it is I/O.
            // Not on every call either — nothing takes the folder away again,
            // and it was about 21,600 creations a day of a folder that was
            // already there.
            Directory.CreateDirectory(_dataFolderPath);
        }

        SqliteConnection connection = new(_connectionString);
        await connection.OpenAsync(ct);

        if (first)
        {
            // WAL, so a read never waits on the write in progress. It is a
            // property of the database file rather than of the connection:
            // once set it stays set, for every connection that ever opens this
            // file, so setting it again bought nothing.
            await Execute(connection, "PRAGMA journal_mode=WAL;", ct);

            // After the pragma, never before. A second caller that arrives in
            // between does the work again rather than carrying on as though
            // WAL were already in force.
            TimesPrepared++;
            _prepared = true;
        }

        // Genuinely per connection, and it stays: without this SQLite does not
        // enforce the foreign keys it was given, and it is off again on the
        // next connection.
        await Execute(connection, "PRAGMA foreign_keys=ON;", ct);

        return connection;
    }

    /// <summary>
    /// Brings the database up to date, and does nothing at all when it already
    /// is.
    /// </summary>
    /// <remarks>
    /// <c>PRAGMA user_version</c> carries the number of the last migration that
    /// ran. Each migration runs inside a transaction with the version bump, so
    /// a migration that fails half way leaves the version where it was rather
    /// than a database that is neither one shape nor the other.
    /// </remarks>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await OpenAsync(ct);

        long version = await CurrentVersion(connection, ct);

        foreach ((long number, string sql) in Migrations())
        {
            if (number <= version)
            {
                continue;
            }

            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

            await Execute(connection, sql, ct, transaction);

            // Interpolated rather than parameterised: a pragma takes no
            // parameters, and the value is a number this assembly owns — it
            // comes from a file name in its own resources, never from input.
            await Execute(connection, $"PRAGMA user_version={number};", ct, transaction);

            await transaction.CommitAsync(ct);
        }
    }

    /// <summary>
    /// The migrations that ship, in order, with the number each file's name
    /// begins with.
    /// </summary>
    private static IEnumerable<(long Number, string Sql)> Migrations()
    {
        Assembly assembly = typeof(Store).Assembly;

        return assembly
            .GetManifestResourceNames()
            .Where(name => name.Contains(".Migrations.", StringComparison.Ordinal)
                           && name.EndsWith(".sql", StringComparison.Ordinal))
            .Select(name => (Number: NumberOf(name), Name: name))
            .OrderBy(migration => migration.Number)
            .Select(migration => (migration.Number, Sql: Read(assembly, migration.Name)));
    }

    private static long NumberOf(string resourceName)
    {
        // "…Storage.Migrations.001-initial.sql" → 1. The number is the order
        // migrations run in, so a file that cannot be numbered is a file that
        // would run at an unpredictable point.
        string fileName = resourceName[(resourceName.IndexOf(".Migrations.", StringComparison.Ordinal) + ".Migrations.".Length)..];
        string digits = new([.. fileName.TakeWhile(char.IsAsciiDigit)]);

        if (digits.Length == 0)
        {
            throw new InvalidOperationException($"The migration '{resourceName}' does not begin with a number.");
        }

        return long.Parse(digits);
    }

    private static string Read(Assembly assembly, string resourceName)
    {
        using Stream stream = assembly.GetManifestResourceStream(resourceName)
                              ?? throw new InvalidOperationException($"The migration '{resourceName}' is not in the assembly.");
        using StreamReader reader = new(stream);

        return reader.ReadToEnd();
    }

    private static async Task<long> CurrentVersion(SqliteConnection connection, CancellationToken ct)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";

        return Convert.ToInt64(await command.ExecuteScalarAsync(ct));
    }

    private static async Task Execute(
        SqliteConnection connection,
        string sql,
        CancellationToken ct,
        SqliteTransaction? transaction = null)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        await command.ExecuteNonQueryAsync(ct);
    }
}
