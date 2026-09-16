using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>The preferences the owner saved per library, in <c>library_preferences</c>.</summary>
/// <remarks>
/// A library with no row has no quality, codec any, specials off and no tags
/// (<c>docs/specs/show-list.md</c>), which is what a fresh <see cref="LibraryPreferences"/> is.
/// </remarks>
public sealed class LibraryPreferencesRepository(Store database)
{
    public async Task<LibraryPreferences> ForAsync(string libraryId, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT quality, codec, specials, wishes, musts, forbidden, english_only
            FROM library_preferences WHERE library_id = $library;
            """;

        command.Parameters.AddWithValue("$library", libraryId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        if (!await reader.ReadAsync(ct))
        {
            return new LibraryPreferences(libraryId);
        }

        return new(libraryId)
        {
            Quality = reader.IsDBNull(0) ? null : reader.GetString(0),
            Codec = reader.GetString(1),
            Specials = reader.GetInt64(2) != 0,
            Wishes = TagList.Read(reader.GetString(3)),
            Musts = TagList.Read(reader.GetString(4)),
            Forbidden = TagList.Read(reader.GetString(5)),
            EnglishOnly = reader.GetInt64(6) != 0,
        };
    }

    public async Task SaveAsync(LibraryPreferences preferences, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO library_preferences (library_id, quality, codec, specials, wishes, musts, forbidden, english_only)
            VALUES ($library, $quality, $codec, $specials, $wishes, $musts, $forbidden, $english)
            ON CONFLICT(library_id) DO UPDATE SET
                quality = excluded.quality,
                codec = excluded.codec,
                specials = excluded.specials,
                wishes = excluded.wishes,
                musts = excluded.musts,
                forbidden = excluded.forbidden,
                english_only = excluded.english_only;
            """;

        command.Parameters.AddWithValue("$library", preferences.LibraryId);
        command.Parameters.AddWithValue("$quality", (object?)preferences.Quality ?? DBNull.Value);
        command.Parameters.AddWithValue("$codec", preferences.Codec);
        command.Parameters.AddWithValue("$specials", preferences.Specials ? 1 : 0);
        command.Parameters.AddWithValue("$wishes", TagList.Write(preferences.Wishes));
        command.Parameters.AddWithValue("$musts", TagList.Write(preferences.Musts));
        command.Parameters.AddWithValue("$forbidden", TagList.Write(preferences.Forbidden));
        command.Parameters.AddWithValue("$english", preferences.EnglishOnly ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct);
    }
}
