using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// What has been grabbed, and what became of it.
/// </summary>
/// <remarks>
/// <para>
/// This is the record that lets everything else recover. The magnet is kept
/// after the client has taken it, so a torrent the client has forgotten can be
/// re-added rather than downloaded again; the episodes a grab covers are kept,
/// so a season pack that fails can put all of them back to missing at once.
/// </para>
/// <para>
/// It is also the one thing that knows which episodes a hash was fetched for,
/// which is why blacklisting a failed hash lives here and not in the client.
/// </para>
/// </remarks>
public sealed class GrabRepository(Database database)
{
    /// <summary>Records a grab, with every episode it answers for.</summary>
    public async Task RecordAsync(
        EpisodeKey episode,
        string showTitle,
        string releaseTitle,
        string source,
        string? infoHash,
        string? magnet,
        IReadOnlyList<EpisodeKey> covers,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO grabs (show_id, season, episode, release_title, info_hash, source, magnet, grabbed_at, state, covers)
            VALUES ($show, $season, $episode, $release, $hash, $source, $magnet, $at, $state, $covers);
            """;

        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$release", releaseTitle);

        // Upper case here as everywhere: the wire, the store and the page all
        // spell a hash the same way or they do not find each other.
        command.Parameters.AddWithValue("$hash", (object?)infoHash?.ToUpperInvariant() ?? DBNull.Value);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$magnet", (object?)magnet ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$state", nameof(GrabState.Grabbed).ToLowerInvariant());
        command.Parameters.AddWithValue(
            "$covers",
            JsonSerializer.Serialize(covers.Select(one => new[] { one.ShowId, one.Season, one.Number })));

        _ = showTitle;

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Everything not finished with, as recovery needs it.</summary>
    public async Task<IReadOnlyList<StoredDownload>> OpenAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT info_hash, magnet, release_title, state FROM grabs
            WHERE info_hash IS NOT NULL AND state NOT IN ('done', 'failed');
            """;

        List<StoredDownload> open = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            open.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetString(2),
                Enum.TryParse(reader.GetString(3), ignoreCase: true, out GrabState state) ? state : GrabState.Grabbed));
        }

        return open;
    }

    /// <summary>
    /// Moves a grab along.
    /// </summary>
    /// <remarks>
    /// Every hash is upper-cased on the way in and on the way to a query, so
    /// matching is exact. A collation that ignored case would be defending
    /// against rows this code did not write, and nothing here can produce one —
    /// a mutation removing it survived every test, which is how it was noticed.
    /// </remarks>
    public async Task StateAsync(string infoHash, GrabState state, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "UPDATE grabs SET state = $state WHERE info_hash = $hash;";
        command.Parameters.AddWithValue("$state", state.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$hash", infoHash.ToUpperInvariant());

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A download that failed: blacklisted by hash, and every episode it
    /// covered put back to missing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Both halves matter and they are one transaction. Blacklisting without
    /// returning the episodes leaves them looking grabbed for ever; returning
    /// them without blacklisting has the next search choose the same release
    /// and fail the same way, for as long as the plugin runs.
    /// </para>
    /// <para>
    /// A season pack that fails puts back every episode it answered for, which
    /// is what the covers list is kept for.
    /// </para>
    /// </remarks>
    public async Task<int> FailedAsync(string infoHash, string reason, DateTimeOffset at, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        string hash = infoHash.ToUpperInvariant();
        List<EpisodeKey> covered = [];

        await using (SqliteCommand reading = connection.CreateCommand())
        {
            reading.Transaction = transaction;
            reading.CommandText = "SELECT covers FROM grabs WHERE info_hash = $hash;";
            reading.Parameters.AddWithValue("$hash", hash);

            await using SqliteDataReader reader = await reading.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                foreach (int[] one in JsonSerializer.Deserialize<int[][]>(reader.GetString(0)) ?? [])
                {
                    if (one.Length == 3)
                    {
                        covered.Add(new(one[0], one[1], one[2]));
                    }
                }
            }
        }

        await using (SqliteCommand marking = connection.CreateCommand())
        {
            marking.Transaction = transaction;
            marking.CommandText = "UPDATE grabs SET state = 'failed' WHERE info_hash = $hash;";
            marking.Parameters.AddWithValue("$hash", hash);

            await marking.ExecuteNonQueryAsync(ct);
        }

        await using (SqliteCommand refusing = connection.CreateCommand())
        {
            refusing.Transaction = transaction;

            // The hash, not the title: another release of the same episode is
            // still worth having, and it is this torrent that would not
            // download.
            refusing.CommandText =
                """
                INSERT INTO blacklist (key, reason, at, until) VALUES ($key, $reason, $at, NULL)
                ON CONFLICT(key) DO UPDATE SET reason = $reason, at = $at;
                """;
            refusing.Parameters.AddWithValue("$key", hash);
            refusing.Parameters.AddWithValue("$reason", reason);
            refusing.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));

            await refusing.ExecuteNonQueryAsync(ct);
        }

        foreach (EpisodeKey episode in covered)
        {
            await using SqliteCommand missing = connection.CreateCommand();

            missing.Transaction = transaction;

            // Back to missing, and the attempt count is left alone —
            // B2: a download that failed is not an attempt the episode spent.
            missing.CommandText =
                """
                UPDATE episodes SET state = $state
                WHERE show_id = $show AND season = $season AND episode = $episode AND state <> 'notaired';
                """;
            missing.Parameters.AddWithValue("$state", EpisodeStates.Missing);
            missing.Parameters.AddWithValue("$show", episode.ShowId);
            missing.Parameters.AddWithValue("$season", episode.Season);
            missing.Parameters.AddWithValue("$episode", episode.Number);

            await missing.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return covered.Count;
    }

    /// <summary>Every key the profile should refuse, for the decide stage.</summary>
    public async Task<IReadOnlySet<string>> BlacklistedAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = "SELECT key FROM blacklist WHERE until IS NULL OR until > $now;";
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));

        HashSet<string> keys = new(StringComparer.Ordinal);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            keys.Add(reader.GetString(0));
        }

        return keys;
    }
}
