using System.Globalization;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// When the cycle last finished, in <c>cadences</c> — what the plugin's own
/// clock reads to work out when the next one is due. See <c>Hosting/Clock.cs</c>.
/// </summary>
/// <remarks>
/// A cycle with no row has never run and is due at once, which is the right
/// answer on a fresh install. Kept in the database rather than in memory for
/// the same reason <c>RunRepository</c> is: a restart must not make every
/// cadence forget it has ever run.
/// </remarks>
public sealed class CadenceRepository(Store database)
{
    /// <summary>Every cadence's last finish, by name.</summary>
    public async Task<IReadOnlyDictionary<string, DateTimeOffset>> LastFinishedAsync(CancellationToken ct)
    {
        Dictionary<string, DateTimeOffset> finished = new(StringComparer.Ordinal);

        foreach (StoredRow row in await database.QueryAsync("SELECT name, last_finished_at FROM cadences;", null, ct))
        {
            finished[row.Text("name")] = DateTimeOffset.Parse(row.Text("last_finished_at"), CultureInfo.InvariantCulture);
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
        await database.ExecuteAsync(
            """
            INSERT INTO cadences (name, last_finished_at) VALUES ($name, $at)
            ON CONFLICT(name) DO UPDATE SET last_finished_at = excluded.last_finished_at;
            """,
            new Dictionary<string, object?>
            {
                ["$name"] = name,
                ["$at"] = at.ToString("O", CultureInfo.InvariantCulture),
            },
            ct);
    }
}
