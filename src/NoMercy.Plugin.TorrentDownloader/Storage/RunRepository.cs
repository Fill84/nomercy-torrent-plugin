using System.Globalization;
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
        // One transaction, so the trim never runs without the insert it trims for.
        await database.TransactionAsync(
            async session =>
            {
                await session.ExecuteAsync(
                    "INSERT INTO runs (started_at, ended_at, outcome) VALUES ($started, $ended, $outcome);",
                    new Dictionary<string, object?>
                    {
                        ["$started"] = run.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                        ["$ended"] = run.EndedAt.ToString("O", CultureInfo.InvariantCulture),
                        ["$outcome"] = run.How.ToString(),
                    },
                    ct);

                await session.ExecuteAsync(
                    "DELETE FROM runs WHERE rowid NOT IN (SELECT rowid FROM runs ORDER BY ended_at DESC LIMIT $kept);",
                    new Dictionary<string, object?> { ["$kept"] = Kept },
                    ct);
            },
            ct);
    }

    /// <summary>The run that ended last, or null when none ever has.</summary>
    public async Task<LastRun?> LastAsync(CancellationToken ct)
    {
        IReadOnlyList<StoredRow> rows = await database.QueryAsync(
            "SELECT started_at, ended_at, outcome FROM runs ORDER BY ended_at DESC LIMIT 1;",
            null,
            ct);

        return rows is [StoredRow row]
            ? new(
                DateTimeOffset.Parse(row.Text("started_at"), CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(row.Text("ended_at"), CultureInfo.InvariantCulture),
                Enum.Parse<RunEnd>(row.Text("outcome")))
            : null;
    }
}
