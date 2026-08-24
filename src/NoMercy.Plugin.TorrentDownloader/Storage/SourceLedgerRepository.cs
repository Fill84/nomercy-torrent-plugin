using System.Globalization;
using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// The last answer of every source, in <c>source_reports</c>.
/// </summary>
/// <remarks>
/// One row per source, replaced on every ask. A history of asks would grow
/// without bound and answer a question nobody has: what the Sources page says
/// is what a site did <em>last</em>, because that is what decides whether to
/// ask it again.
/// </remarks>
public sealed class SourceLedgerRepository(Store database) : ISourceLedger
{
    public async Task RecordAsync(SourceAnswer answer, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO source_reports (name, at, rows, refusal, duration_ms)
            VALUES ($name, $at, $rows, $refusal, $duration)
            ON CONFLICT(name) DO UPDATE SET
                at = $at, rows = $rows, refusal = $refusal, duration_ms = $duration;
            """;

        command.Parameters.AddWithValue("$name", answer.Name);
        command.Parameters.AddWithValue("$at", answer.At.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$rows", answer.Rows);
        command.Parameters.AddWithValue("$refusal", (object?)answer.Refusal ?? DBNull.Value);
        command.Parameters.AddWithValue("$duration", (long)answer.Duration.TotalMilliseconds);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every source that has ever answered, by name.</summary>
    public async Task<IReadOnlyDictionary<string, SourceAnswer>> AllAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT name, at, rows, refusal, duration_ms FROM source_reports;";

        Dictionary<string, SourceAnswer> answers = new(StringComparer.OrdinalIgnoreCase);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            answers[reader.GetString(0)] = new(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                TimeSpan.FromMilliseconds(reader.GetInt64(4)));
        }

        return answers;
    }
}
