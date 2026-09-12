using System.Globalization;
using Microsoft.Data.Sqlite;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// When each cadence last finished, in <c>cadences</c> — what the plugin's own
/// clock reads to decide what is due next. See <c>Hosting/Clock.cs</c>.
/// </summary>
/// <remarks>
/// A cadence with no row has never run and is due at once, which is the right
/// answer on a fresh install. Kept in the database rather than in memory for
/// the same reason <c>RunRepository</c> is: a restart must not make every
/// cadence forget it has ever run.
/// </remarks>
public sealed class CadenceRepository(Store database)
{
    /// <summary>Every cadence's last finish, by name.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastFinishedAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name, last_finished_at FROM cadences;";

        Dictionary<string, DateTimeOffset> finished = new(StringComparer.Ordinal);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            finished[reader.GetString(0)] = DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture);
        }

        return finished;
    }

    /// <summary>Records that the cadence named <paramref name="name"/> finished at <paramref name="at"/>.</summary>
    /// <remarks>
    /// Upserted, because a cadence's row either does not exist yet — it has
    /// never run — or holds the one previous finish, which this replaces.
    /// </remarks>
    public async Task RecordFinishedAsync(string name, DateTimeOffset at, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO cadences (name, last_finished_at) VALUES ($name, $at)
            ON CONFLICT(name) DO UPDATE SET last_finished_at = excluded.last_finished_at;
            """;

        command.Parameters.AddWithValue("$name", name);
        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));

        await command.ExecuteNonQueryAsync(ct);
    }
}
