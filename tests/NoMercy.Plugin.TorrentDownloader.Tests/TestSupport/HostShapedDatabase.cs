using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// <see cref="IPluginDatabase"/> as the media server's in-process host gives it.
/// </summary>
/// <remarks>
/// <para>
/// Read off the server's <c>dev</c> branch on 15 October 2026
/// (<c>src/NoMercy.Plugins/Storage/PluginDatabase.cs</c>), and written down in
/// <c>docs/09-host-contract.md</c>: one <see cref="SqliteConnection"/> for the
/// life of the database with no lock around it; the count of applied migrations
/// in <c>__nomercy_plugin_schema (applied INTEGER NOT NULL)</c>, made on open;
/// <see cref="MigrateAsync"/> runs <c>statements[MAX(applied)..]</c>, each in a
/// transaction of its own with <c>index + 1</c> inserted; a null parameter bound
/// as <c>NULL</c>; rows answered as column name to value, with SQLite's own
/// types (<c>long</c>, <c>double</c>, <c>string</c>, <c>byte[]</c>).
/// </para>
/// <para>
/// Where this and the server disagree, the server is right and this is wrong: a
/// test here that passes against a double the host does not match proves
/// nothing about the plugin on a server.
/// </para>
/// </remarks>
public sealed class HostShapedDatabase : IPluginDatabase
{
    public const string VersionTable = "__nomercy_plugin_schema";

    private readonly SqliteConnection _connection;

    private HostShapedDatabase(SqliteConnection connection)
    {
        _connection = connection;
    }

    public static async Task<HostShapedDatabase> OpenAsync(string path, CancellationToken ct = default)
    {
        if (Path.GetDirectoryName(path) is string folder)
        {
            Directory.CreateDirectory(folder);
        }

        SqliteConnection connection = new(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
        }.ToString());

        await connection.OpenAsync(ct);

        HostShapedDatabase database = new(connection);

        await database.ExecuteAsync($"CREATE TABLE IF NOT EXISTS {VersionTable} (applied INTEGER NOT NULL)", ct: ct);

        return database;
    }

    public async Task MigrateAsync(IReadOnlyList<string> statements, CancellationToken ct = default)
    {
        int applied = Convert.ToInt32(await ScalarAsync($"SELECT MAX(applied) FROM {VersionTable}", ct: ct) ?? 0);

        for (int index = applied; index < statements.Count; index++)
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await _connection.BeginTransactionAsync(ct);

            await using (SqliteCommand statement = Command(statements[index], null, transaction))
            {
                await statement.ExecuteNonQueryAsync(ct);
            }

            await using (SqliteCommand counted = Command(
                             $"INSERT INTO {VersionTable} (applied) VALUES ($applied)",
                             new Dictionary<string, object?> { ["$applied"] = index + 1 },
                             transaction))
            {
                await counted.ExecuteNonQueryAsync(ct);
            }

            await transaction.CommitAsync(ct);
        }
    }

    public async Task<int> ExecuteAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        await using SqliteCommand command = Command(sql, parameters, null);

        return await command.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        List<IReadOnlyDictionary<string, object?>> rows = [];

        await foreach (IReadOnlyDictionary<string, object?> row in QueryStreamAsync(sql, parameters, ct))
        {
            rows.Add(row);
        }

        return rows;
    }

    public async IAsyncEnumerable<IReadOnlyDictionary<string, object?>> QueryStreamAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await using SqliteCommand command = Command(sql, parameters, null);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            Dictionary<string, object?> row = [];

            for (int column = 0; column < reader.FieldCount; column++)
            {
                row[reader.GetName(column)] = reader.IsDBNull(column) ? null : reader.GetValue(column);
            }

            yield return row;
        }
    }

    public async Task<object?> ScalarAsync(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters = null,
        CancellationToken ct = default)
    {
        await using SqliteCommand command = Command(sql, parameters, null);
        object? value = await command.ExecuteScalarAsync(ct);

        return value is DBNull ? null : value;
    }

    public async ValueTask DisposeAsync()
    {
        await _connection.DisposeAsync();
    }

    private SqliteCommand Command(
        string sql,
        IReadOnlyDictionary<string, object?>? parameters,
        SqliteTransaction? transaction)
    {
        SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;

        foreach ((string name, object? value) in parameters ?? new Dictionary<string, object?>())
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command;
    }
}

/// <summary>
/// <see cref="IPluginStorage"/> with the host's database behind it, in one folder.
/// </summary>
/// <remarks>
/// The host opens <c>plugins/data/&lt;plugin id&gt;/&lt;name&gt;.sqlite</c>, the same
/// folder it hands the plugin as <see cref="IPluginContext.DataFolderPath"/>
/// (<c>PluginHostStorage</c>, <c>PluginLoader</c>). Only the database is here; the
/// folder scopes are not part of what this plugin uses.
/// </remarks>
public sealed class HostShapedStorage(string folder) : IPluginStorage
{
    /// <summary>How many times a database was opened through this storage.</summary>
    public int Opens => _opens;

    private int _opens;

    public IPluginStorageScope Private => throw NotUsed(nameof(Private));

    public IPluginStorageScope Temp => throw NotUsed(nameof(Temp));

    public IPluginStorageScope Derived => throw NotUsed(nameof(Derived));

    public IPluginUserScope ForUser => throw NotUsed(nameof(ForUser));

    public Task<IPluginStorageScope> PathAsync(string folderId, CancellationToken ct = default)
    {
        throw NotUsed(nameof(PathAsync));
    }

    public async Task<IPluginDatabase> OpenDatabaseAsync(string name, CancellationToken ct = default)
    {
        // The host refuses a name that is not a bare file name; the plugin only
        // ever passes its own constant, so this double refuses it the same way
        // rather than resolving it somewhere the host never would.
        if (name != Path.GetFileName(name) || name.Contains("..", StringComparison.Ordinal))
        {
            throw new ArgumentException($"'{name}' is not a bare database name.", nameof(name));
        }

        Interlocked.Increment(ref _opens);

        return await HostShapedDatabase.OpenAsync(Path.Combine(folder, $"{name}.sqlite"), ct);
    }

    private static NotSupportedException NotUsed(string member)
    {
        return new NotSupportedException($"This plugin does not use IPluginStorage.{member}.");
    }
}
