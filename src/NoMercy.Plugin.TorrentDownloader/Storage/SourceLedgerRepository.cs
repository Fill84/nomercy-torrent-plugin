using System.Globalization;
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
        await database.ExecuteAsync(
            """
            INSERT INTO source_reports (name, at, rows, refusal, duration_ms)
            VALUES ($name, $at, $rows, $refusal, $duration)
            ON CONFLICT(name) DO UPDATE SET
                at = $at, rows = $rows, refusal = $refusal, duration_ms = $duration;
            """,
            new Dictionary<string, object?>
            {
                ["$name"] = answer.Name,
                ["$at"] = answer.At.ToString("O", CultureInfo.InvariantCulture),
                ["$rows"] = answer.Rows,
                ["$refusal"] = answer.Refusal,
                ["$duration"] = (long)answer.Duration.TotalMilliseconds,
            },
            ct);
    }

    /// <summary>Every source that has ever answered, by name.</summary>
    public async Task<IReadOnlyDictionary<string, SourceAnswer>> AllAsync(CancellationToken ct)
    {
        Dictionary<string, SourceAnswer> answers = new(StringComparer.OrdinalIgnoreCase);

        foreach (StoredRow row in await database.QueryAsync(
                     "SELECT name, at, rows, refusal, duration_ms FROM source_reports;",
                     null,
                     ct))
        {
            answers[row.Text("name")] = new(
                row.Text("name"),
                DateTimeOffset.Parse(row.Text("at"), CultureInfo.InvariantCulture),
                row.Int("rows"),
                row.TextOrNull("refusal"),
                TimeSpan.FromMilliseconds(row.Long("duration_ms")));
        }

        return answers;
    }
}
