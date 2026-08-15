using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// The <c>name_pool</c> table: the release names the feeds carried, kept
/// between one stage and the next.
/// </summary>
/// <remarks>
/// On disk rather than in memory because it outlives the cycle that filled it.
/// A harvest interrupted halfway by a restart has already written what it read,
/// so the search that follows starts from those names instead of asking every
/// feed all over again — and a name harvested last week still answers for the
/// episode nobody has found yet.
/// </remarks>
public sealed class NamePoolRepository(Database database) : INamePool
{
    public async Task AddAsync(IReadOnlyList<PooledName> names, CancellationToken ct)
    {
        if (names.Count == 0)
        {
            // Nothing to write is not a write. A cycle where every feed was
            // down should not open the database to say so.
            return;
        }

        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        foreach (PooledName name in names)
        {
            await using SqliteCommand upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO name_pool (normalised, title, source, seen_at)
                VALUES ($key, $title, $source, $seenAt)
                ON CONFLICT (normalised, title) DO UPDATE SET
                    source  = excluded.source,
                    seen_at = excluded.seen_at;
                """;

            // The feed that carried it most recently wins, and the time with
            // it: a name nobody has seen for months is the one worth forgetting
            // first, and that is decided by seen_at.
            upsert.Parameters.AddWithValue("$key", name.Key);
            upsert.Parameters.AddWithValue("$title", name.Title);
            upsert.Parameters.AddWithValue("$source", name.Source);
            upsert.Parameters.AddWithValue("$seenAt", name.SeenAt.ToString("O"));

            await upsert.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }
}
