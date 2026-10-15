using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// The plugin's own SQLite database, opened by the host, and the migrations that
/// shape it.
/// </summary>
/// <remarks>
/// <para>
/// A JSON file was 0.3.4's store and is the wrong shape: every write rewrites
/// everything, two cadences writing at once lose each other's work, and asking
/// what is still missing means loading all of it.
/// </para>
/// <para>
/// The host opens it (<c>IPluginStorage.OpenDatabaseAsync</c>) rather than the
/// plugin shipping a SQLite of its own: the marketplace refuses
/// <c>Microsoft.Data.Sqlite</c> and <c>SQLitePCLRaw</c> in a plugin folder
/// (media-server #476). What the host's database does and does not do is in
/// <c>docs/09-host-contract.md</c> § The database.
/// </para>
/// </remarks>
public sealed class Store : IAsyncDisposable
{
    /// <summary>The name the host opens it under: <c>torrent-downloader.sqlite</c> in the plugin's data folder.</summary>
    public const string DatabaseName = "torrent-downloader";

    /// <summary>What 0.8.1 and every version before it called the file, in the same folder.</summary>
    public const string OldFileName = "torrent-downloader.db";

    /// <summary>
    /// Where the host counts the migrations it has applied. The host's own table,
    /// named here because the carry-over has to start its count.
    /// </summary>
    private const string HostCountTable = "__nomercy_plugin_schema";

    private readonly string _dataFolderPath;

    private readonly Func<IPluginStorage> _storage;

    /// <summary>
    /// One caller at a time, for everything.
    /// </summary>
    /// <remarks>
    /// The host gives one connection and no lock around it. Two callers on it at
    /// once is a page reading a run's half-written transaction, or a second
    /// <c>BEGIN</c> refused because the first has not ended. The plugin's own
    /// SQLite had a connection per call and the database's own locking; this is
    /// what replaces that.
    /// </remarks>
    private readonly SemaphoreSlim _gate = new(1, 1);

    private IPluginDatabase? _database;

    /// <remarks>
    /// Touches nothing. The plugin constructs this while the server is still
    /// coming up, and <c>Initialize</c> is not allowed to do I/O; the database is
    /// opened when it is first used. The storage is asked for then too, because
    /// on a host that wired none the facade refuses by name.
    /// </remarks>
    public Store(string dataFolderPath, Func<IPluginStorage> storage)
    {
        _dataFolderPath = dataFolderPath;
        _storage = storage;
    }

    /// <summary>Brings the database up to date, and does nothing at all when it already is.</summary>
    public async Task MigrateAsync(CancellationToken ct)
    {
        await InsideAsync(
            async database =>
            {
                await database.MigrateAsync(Migrations(), ct);

                return true;
            },
            ct);
    }

    public Task<IReadOnlyList<StoredRow>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return InsideAsync(database => new StoreSession(database).QueryAsync(sql, parameters, ct), ct);
    }

    public Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        return InsideAsync(database => database.ExecuteAsync(sql, parameters, ct), ct);
    }

    public Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        return InsideAsync(database => database.ScalarAsync(sql, parameters, ct), ct);
    }

    /// <summary>
    /// Runs <paramref name="work"/> as one transaction: all of it, or none of it.
    /// </summary>
    /// <remarks>
    /// The host offers no transaction, so it is SQL on its one connection, which
    /// is safe only because the gate is held from <c>BEGIN</c> to the end.
    /// <c>IMMEDIATE</c> takes the write lock at the start rather than at the first
    /// write, so a transaction that reads and then writes cannot be refused half
    /// way because another connection to the file wrote in between.
    /// </remarks>
    public Task<T> TransactionAsync<T>(Func<StoreSession, Task<T>> work, CancellationToken ct)
    {
        return InsideAsync(
            async database =>
            {
                StoreSession session = new(database);

                await session.ExecuteAsync("BEGIN IMMEDIATE;", null, ct);

                T result;

                try
                {
                    result = await work(session);
                    await session.ExecuteAsync("COMMIT;", null, ct);
                }
                catch
                {
                    await RollBackAsync(session);

                    throw;
                }

                return result;
            },
            ct);
    }

    public async Task TransactionAsync(Func<StoreSession, Task> work, CancellationToken ct)
    {
        await TransactionAsync(
            async session =>
            {
                await work(session);

                return true;
            },
            ct);
    }

    public async ValueTask DisposeAsync()
    {
        // Waits for a call in progress, so its connection is not closed under it,
        // but not for ever: a host unloading the plugin must not hang on a query.
        bool entered = await _gate.WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            if (_database is not null)
            {
                await _database.DisposeAsync();
                _database = null;
            }
        }
        finally
        {
            if (entered)
            {
                _gate.Release();
            }
        }
    }

    private async Task<T> InsideAsync<T>(Func<IPluginDatabase, Task<T>> work, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);

        try
        {
            return await work(_database ??= await OpenAsync(ct));
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The host's database, with an owner's 0.8.1 database carried into it first.
    /// </summary>
    private async Task<IPluginDatabase> OpenAsync(CancellationToken ct)
    {
        string carried = Path.Combine(_dataFolderPath, $"{DatabaseName}.sqlite");

        CarryOver(carried);

        IPluginDatabase database = await _storage().OpenDatabaseAsync(DatabaseName, ct);

        try
        {
            await RefuseAnotherFileAsync(database, carried, ct);

            // WAL so a page reading does not wait on the file while a run writes:
            // a property of the file, which the host does not set. Foreign keys,
            // because SQLite does not enforce the ones it was given unless asked
            // on the connection — and the host's is the only connection there is.
            await database.ExecuteAsync("PRAGMA journal_mode=WAL;", null, ct);
            await database.ExecuteAsync("PRAGMA foreign_keys=ON;", null, ct);

            await StartTheHostsCountAsync(database, ct);
        }
        catch
        {
            await database.DisposeAsync();

            throw;
        }

        return database;
    }

    /// <summary>
    /// Renames <c>torrent-downloader.db</c>, and the files SQLite keeps beside it,
    /// to the name the host opens.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The write-ahead log goes with it, and before it: a server that died leaves
    /// the latest rows only in <c>-wal</c>, and a database moved without its log
    /// is one that never heard of them. The main file goes last, so a move
    /// interrupted half way is finished by the next open rather than mistaken for
    /// a fresh install.
    /// </para>
    /// <para>
    /// A file that cannot be moved is left exactly where it is and the open
    /// refused. On Windows that is an update while the server runs: the old copy
    /// of the plugin still holds the file. Opening the host's database anyway
    /// would make an empty one and migrate it, and the plugin would start again
    /// from nothing while the owner's months of history sat beside it unread.
    /// </para>
    /// </remarks>
    private void CarryOver(string carried)
    {
        string old = Path.Combine(_dataFolderPath, OldFileName);

        if (!File.Exists(old) || File.Exists(carried))
        {
            return;
        }

        try
        {
            foreach (string suffix in (string[])["-wal", "-shm", "-journal"])
            {
                if (File.Exists(old + suffix))
                {
                    // Overwritten: a log beside a database that is not there yet
                    // belongs to nothing, and the old database's own log is the
                    // one that has to come along.
                    File.Move(old + suffix, carried + suffix, overwrite: true);
                }
            }

            File.Move(old, carried);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The plugin's database at {old} could not be moved to {carried} ({failure.Message}). "
                + "Nothing was changed; restart the server to finish the update.",
                failure);
        }
    }

    /// <summary>
    /// Refuses a database the host opened somewhere other than where the plugin's
    /// data is.
    /// </summary>
    /// <remarks>
    /// The host and the plugin are each told the same folder today. Were they ever
    /// told different ones, the host would open an empty database, the migrations
    /// would make it the plugin's from then on, and the carried-over one would lie
    /// unread. Only refused when there is a database here to lose: a host that
    /// keeps every plugin's database elsewhere is entitled to on a fresh install.
    /// </remarks>
    private static async Task RefuseAnotherFileAsync(IPluginDatabase database, string carried, CancellationToken ct)
    {
        if (!File.Exists(carried))
        {
            return;
        }

        string opened = (await database.QueryAsync("PRAGMA database_list;", null, ct))
            .Select(columns => new StoredRow(columns))
            .Where(row => row.TextOrNull("name") == "main")
            .Select(row => row.TextOrNull("file") ?? string.Empty)
            .FirstOrDefault() ?? string.Empty;

        StringComparison paths = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (opened.Length == 0 || !string.Equals(Path.GetFullPath(opened), Path.GetFullPath(carried), paths))
        {
            throw new InvalidOperationException(
                $"The server opened this plugin's database at '{opened}', but its data is at {carried}. "
                + "Nothing was migrated, so nothing there is lost.");
        }
    }

    /// <summary>
    /// Tells the host how many migrations 0.8.1 had already run, the first time
    /// the host opens a database 0.8.1 wrote.
    /// </summary>
    /// <remarks>
    /// 0.8.1 counted in <c>PRAGMA user_version</c>, the host counts rows in a
    /// table of its own and makes that table empty. Left empty, the host would
    /// run <c>001-initial</c> over tables that are already there and the plugin's
    /// first open would fail. The two counts mean the same number only while the
    /// migrations are numbered 1, 2, 3 … with no gap, which
    /// <see cref="Migrations"/> refuses to ship otherwise.
    /// </remarks>
    private static async Task StartTheHostsCountAsync(IPluginDatabase database, CancellationToken ct)
    {
        if (await database.ScalarAsync($"SELECT MAX(applied) FROM {HostCountTable};", null, ct) is not null)
        {
            return;
        }

        long carried = Convert.ToInt64(await database.ScalarAsync("PRAGMA user_version;", null, ct) ?? 0L);

        if (carried > 0)
        {
            await database.ExecuteAsync(
                $"INSERT INTO {HostCountTable} (applied) VALUES ($applied);",
                new Dictionary<string, object?> { ["$applied"] = carried },
                ct);
        }
    }

    /// <summary>Ends the transaction the work began, so the one connection is not left inside it.</summary>
    /// <remarks>
    /// Never with the caller's token: a cancelled run is the likeliest reason to
    /// be here, and a rollback that was cancelled too would leave every later
    /// caller's <c>BEGIN</c> refused. A rollback that fails is one SQLite already
    /// did itself (some errors end the transaction on their own), so the work's
    /// own failure is the one worth throwing.
    /// </remarks>
    private static async Task RollBackAsync(StoreSession session)
    {
        try
        {
            await session.ExecuteAsync("ROLLBACK;", null, CancellationToken.None);
        }
        catch (Exception rollback) when (rollback is not OutOfMemoryException)
        {
            // Already rolled back by SQLite; nothing is left open.
        }
    }

    /// <summary>The migrations that ship, in number order.</summary>
    /// <remarks>
    /// Written into the assembly as code by the build (<c>ShippedFiles</c>), not
    /// read back as embedded resources: that needs
    /// <c>Assembly.GetManifestResourceStream</c>, which the marketplace scan
    /// refuses (media-server #476). They are inside the assembly either way, so a
    /// migration that failed to deploy still cannot look like a database that was
    /// already up to date.
    /// </remarks>
    private static IReadOnlyList<string> Migrations()
    {
        (long Number, string Sql)[] ordered = [.. ShippedFiles.Migrations
            .Select(migration => (Number: NumberOf(migration.Name), migration.Sql))
            .OrderBy(migration => migration.Number)];

        // The host counts by place in this list and 0.8.1 counted by number; a
        // gap would make every migration after it run one place early.
        for (int place = 0; place < ordered.Length; place++)
        {
            if (ordered[place].Number != place + 1)
            {
                throw new InvalidOperationException(
                    $"The migrations are not numbered 1 to {ordered.Length} without a gap: number {place + 1} is missing.");
            }
        }

        return [.. ordered.Select(migration => migration.Sql)];
    }

    private static long NumberOf(string fileName)
    {
        // "001-initial.sql" → 1. The number is the order migrations run in, so a
        // file that cannot be numbered is a file that would run at an
        // unpredictable point.
        string digits = new([.. fileName.TakeWhile(char.IsAsciiDigit)]);

        if (digits.Length == 0)
        {
            throw new InvalidOperationException($"The migration '{fileName}' does not begin with a number.");
        }

        return long.Parse(digits);
    }
}

/// <summary>The database while the store's gate is held: what a transaction's work is given.</summary>
/// <remarks>
/// Its own type, not the store, so work inside a transaction cannot call back
/// into the store and wait on the gate it is already holding.
/// </remarks>
public sealed class StoreSession(IPluginDatabase database)
{
    public async Task<IReadOnlyList<StoredRow>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        CancellationToken ct)
    {
        return [.. (await database.QueryAsync(sql, parameters, ct)).Select(columns => new StoredRow(columns))];
    }

    public Task<int> ExecuteAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        return database.ExecuteAsync(sql, parameters, ct);
    }

    public Task<object?> ScalarAsync(string sql, IReadOnlyDictionary<string, object?>? parameters, CancellationToken ct)
    {
        return database.ScalarAsync(sql, parameters, ct);
    }
}
