using System.Globalization;
using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>One search run, from its start to how it ended.</summary>
public sealed record LastRun(DateTimeOffset StartedAt, DateTimeOffset EndedAt, RunEnd How);

/// <summary>
/// How search runs ended, in <c>runs</c>.
/// </summary>
/// <remarks>
/// Kept in the database rather than in memory so the status bar can say when
/// the plugin last ran after the server has been restarted. Only the latest are
/// kept: the question the page asks is "how did the last one end", and a table
/// that grew with every run would answer nothing more for it.
/// </remarks>
public sealed class RunRepository(Store database)
{
    /// <summary>How many runs are kept.</summary>
    public const int Kept = 100;

    public async Task RecordAsync(LastRun run, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO runs (started_at, ended_at, outcome) VALUES ($started, $ended, $outcome);
            DELETE FROM runs
            WHERE rowid NOT IN (SELECT rowid FROM runs ORDER BY ended_at DESC LIMIT $kept);
            """;

        command.Parameters.AddWithValue("$started", run.StartedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$ended", run.EndedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$outcome", run.How.ToString());
        command.Parameters.AddWithValue("$kept", Kept);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The run that ended last, or null when none ever has.</summary>
    public async Task<LastRun?> LastAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT started_at, ended_at, outcome FROM runs ORDER BY ended_at DESC LIMIT 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        return new(
            DateTimeOffset.Parse(reader.GetString(0), CultureInfo.InvariantCulture),
            DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
            Enum.Parse<RunEnd>(reader.GetString(2)));
    }
}
