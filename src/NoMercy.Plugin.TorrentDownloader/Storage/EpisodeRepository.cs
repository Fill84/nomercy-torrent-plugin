using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>
/// The <c>episodes</c> table: what the library says, plus the two things only
/// this plugin knows.
/// </summary>
/// <remarks>
/// The table is a derived cache. Everything in it except <c>attempts</c> and
/// <c>last_search_at</c> is rewritten from the library on every maintenance
/// pass, and a row for an episode the library no longer has is deleted.
/// </remarks>
public sealed class EpisodeRepository(Store database)
{
    /// <summary>
    /// Makes the table say exactly what <paramref name="derived"/> says, while
    /// keeping this plugin's own count of what it has tried.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The state written is always the derived one. That is what makes
    /// <c>Unavailable</c> temporary: an episode given up on last night is
    /// derived as missing again this morning and gets another turn. 0.3.4
    /// filtered unavailable episodes out of the refresh and preserved their
    /// state, so an episode that went unavailable once was invisible for ever.
    /// </para>
    /// <para>
    /// <c>attempts</c> and <c>last_search_at</c> are the exception and are left
    /// exactly as they were. They are not in the library and cannot be derived;
    /// rewriting them would forget, every night, everything the plugin had
    /// learnt about how hard an episode is to find.
    /// </para>
    /// </remarks>
    public async Task ReplaceAsync(IReadOnlyList<TrackedEpisode> derived, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        // A temporary table of the keys that should survive, so the delete is
        // one statement whatever the library's size. Building a NOT IN list of
        // several thousand keys instead would be one enormous statement, and
        // SQLite has a limit on how many terms it will take.
        await using (SqliteCommand create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText =
                "CREATE TEMP TABLE keep (show_id INTEGER NOT NULL, season INTEGER NOT NULL, episode INTEGER NOT NULL);";
            await create.ExecuteNonQueryAsync(ct);
        }

        foreach (TrackedEpisode episode in derived)
        {
            await using SqliteCommand upsert = connection.CreateCommand();
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO keep (show_id, season, episode) VALUES ($show, $season, $episode);

                INSERT INTO episodes
                    (show_id, season, episode, show_title, show_year, library_type,
                     absolute, episode_title, air_date, state, attempts, last_search_at)
                VALUES
                    ($show, $season, $episode, $showTitle, $showYear, $libraryType,
                     $absolute, $episodeTitle, $airDate, $state, 0, NULL)
                ON CONFLICT (show_id, season, episode) DO UPDATE SET
                    show_title    = excluded.show_title,
                    show_year     = excluded.show_year,
                    library_type  = excluded.library_type,
                    absolute      = excluded.absolute,
                    episode_title = excluded.episode_title,
                    air_date      = excluded.air_date,
                    state         = excluded.state;
                """;

            // attempts and last_search_at are deliberately absent from the SET
            // list. Left out, they keep whatever they held.
            upsert.Parameters.AddWithValue("$show", episode.Key.ShowId);
            upsert.Parameters.AddWithValue("$season", episode.Key.Season);
            upsert.Parameters.AddWithValue("$episode", episode.Key.Number);
            upsert.Parameters.AddWithValue("$showTitle", episode.ShowTitle);
            upsert.Parameters.AddWithValue("$showYear", (object?)episode.ShowYear ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$libraryType", LibraryTypeOf(episode.Kind));
            upsert.Parameters.AddWithValue("$absolute", (object?)episode.Absolute ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$episodeTitle", (object?)episode.EpisodeTitle ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$airDate", (object?)episode.AirDate?.ToString("O") ?? DBNull.Value);
            upsert.Parameters.AddWithValue("$state", EpisodeStates.ToStored(episode.State));

            await upsert.ExecuteNonQueryAsync(ct);
        }

        await using (SqliteCommand delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            // Gone from the library means gone from here: the show was removed,
            // or the episode now has a file. Either way the plugin has no more
            // business with it, and a row left behind would keep it in a queue
            // for something already on disk.
            delete.CommandText =
                """
                DELETE FROM episodes
                WHERE NOT EXISTS (
                    SELECT 1 FROM keep
                    WHERE keep.show_id = episodes.show_id
                      AND keep.season = episodes.season
                      AND keep.episode = episodes.episode);

                DROP TABLE keep;
                """;
            await delete.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);
    }

    /// <summary>Every tracked episode, whatever its state.</summary>
    public async Task<IReadOnlyList<TrackedEpisode>> AllAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT show_id, season, episode, show_title, show_year, library_type,
                   absolute, episode_title, air_date, state, attempts, last_search_at
            FROM episodes
            ORDER BY show_id, season, episode;
            """;

        List<TrackedEpisode> episodes = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            episodes.Add(Read(reader));
        }

        return episodes;
    }

    /// <summary>
    /// Counts a search against an episode.
    /// </summary>
    /// <remarks>
    /// The only thing in this repository that moves <c>attempts</c>, and
    /// deliberately so. In 0.3.4 a download that failed burned a search
    /// attempt, so three failed grabs exhausted an episode that had never had a
    /// search go badly — the number went up, which looked like work.
    /// </remarks>
    public async Task RecordSearchAsync(EpisodeKey key, DateTimeOffset at, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episodes
            SET attempts = attempts + 1, last_search_at = $at
            WHERE show_id = $show AND season = $season AND episode = $episode;
            """;
        command.Parameters.AddWithValue("$at", at.ToString("O"));
        command.Parameters.AddWithValue("$show", key.ShowId);
        command.Parameters.AddWithValue("$season", key.Season);
        command.Parameters.AddWithValue("$episode", key.Number);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Gives up on an episode for now.
    /// </summary>
    /// <remarks>
    /// It does not touch <c>attempts</c>: giving up is a consequence of the
    /// attempts already recorded, not another one of them.
    /// </remarks>
    public async Task MarkUnavailableAsync(EpisodeKey key, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE episodes SET state = $state
            WHERE show_id = $show AND season = $season AND episode = $episode;
            """;
        command.Parameters.AddWithValue("$state", EpisodeStates.Unavailable);
        command.Parameters.AddWithValue("$show", key.ShowId);
        command.Parameters.AddWithValue("$season", key.Season);
        command.Parameters.AddWithValue("$episode", key.Number);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static TrackedEpisode Read(SqliteDataReader reader)
    {
        return new(
            new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)),
            reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt32(4),
            KindOf(reader.GetString(5)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : DateOnly.Parse(reader.GetString(8)),
            EpisodeStates.FromStored(reader.GetString(9)),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.GetInt32(10),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)));
    }

    private static string LibraryTypeOf(LibraryKind kind)
    {
        return kind == LibraryKind.Anime ? LibraryKinds.Anime : LibraryKinds.Television;
    }

    private static LibraryKind KindOf(string libraryType)
    {
        return LibraryKinds.TryParse(libraryType, out LibraryKind kind)
            ? kind
            // A row written by a version that knew a type this one does not.
            // Refusing is better than quietly filing an anime as television and
            // searching it under the wrong numbering for ever.
            : throw new InvalidOperationException($"The stored library type '{libraryType}' is not one this plugin knows.");
    }
}
