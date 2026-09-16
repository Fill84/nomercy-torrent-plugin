using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>The settings the owner saved per show, in <c>show_settings</c>.</summary>
/// <remarks>
/// A show with no row is off and not saved (<c>docs/specs/show-list.md</c>), which is why
/// <see cref="ForAsync"/> answers a fresh <see cref="ShowSettings"/> rather than nothing: every show
/// starts there, and a caller that had to tell "no row" from "off" would be one more place to get that
/// wrong.
/// </remarks>
public sealed class ShowSettingsRepository(Store database)
{
    private const string Columns = "show_id, switched_on, saved, quality, codec, specials, wishes, musts, forbidden, english_only";

    public async Task<ShowSettings> ForAsync(int showId, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM show_settings WHERE show_id = $show;";
        command.Parameters.AddWithValue("$show", showId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        return await reader.ReadAsync(ct) ? Read(reader) : new ShowSettings(showId);
    }

    /// <summary>Every show the owner has switched or saved, by show id.</summary>
    public async Task<IReadOnlyDictionary<int, ShowSettings>> AllAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText = $"SELECT {Columns} FROM show_settings;";

        Dictionary<int, ShowSettings> all = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            ShowSettings one = Read(reader);
            all[one.ShowId] = one;
        }

        return all;
    }

    /// <summary>Writes the settings form for one show, which is what makes the show saved.</summary>
    /// <remarks>
    /// Saved is written as true whatever the record carries: this is the Save of the settings form, and
    /// saving that form is exactly what the flag records.
    /// </remarks>
    public async Task SaveAsync(ShowSettings settings, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO show_settings (show_id, switched_on, saved, quality, codec, specials, wishes, musts, forbidden, english_only)
            VALUES ($show, $on, 1, $quality, $codec, $specials, $wishes, $musts, $forbidden, $english)
            ON CONFLICT(show_id) DO UPDATE SET
                switched_on = excluded.switched_on,
                saved = 1,
                quality = excluded.quality,
                codec = excluded.codec,
                specials = excluded.specials,
                wishes = excluded.wishes,
                musts = excluded.musts,
                forbidden = excluded.forbidden,
                english_only = excluded.english_only;
            """;

        command.Parameters.AddWithValue("$show", settings.ShowId);
        command.Parameters.AddWithValue("$on", settings.SwitchedOn ? 1 : 0);
        command.Parameters.AddWithValue("$quality", (object?)settings.Quality ?? DBNull.Value);
        command.Parameters.AddWithValue("$codec", (object?)settings.Codec ?? DBNull.Value);
        command.Parameters.AddWithValue("$specials", settings.Specials is bool specials ? (specials ? 1 : 0) : DBNull.Value);
        command.Parameters.AddWithValue("$wishes", TagList.Write(settings.Wishes));
        command.Parameters.AddWithValue("$musts", TagList.Write(settings.Musts));
        command.Parameters.AddWithValue("$forbidden", TagList.Write(settings.Forbidden));
        command.Parameters.AddWithValue("$english", settings.EnglishOnly is bool english ? (english ? 1 : 0) : DBNull.Value);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Switches a show on or off from the overview's row button.</summary>
    /// <remarks>
    /// Touches nothing else. A show switched off keeps what was saved for it, and a show switched on
    /// that was never saved stays not saved, so it still searches nothing.
    /// </remarks>
    public async Task SwitchAsync(int showId, bool on, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO show_settings (show_id, switched_on) VALUES ($show, $on)
            ON CONFLICT(show_id) DO UPDATE SET switched_on = excluded.switched_on;
            """;

        command.Parameters.AddWithValue("$show", showId);
        command.Parameters.AddWithValue("$on", on ? 1 : 0);

        await command.ExecuteNonQueryAsync(ct);
    }

    private static ShowSettings Read(SqliteDataReader reader)
    {
        return new(reader.GetInt32(0))
        {
            SwitchedOn = reader.GetInt64(1) != 0,
            Saved = reader.GetInt64(2) != 0,
            Quality = reader.IsDBNull(3) ? null : reader.GetString(3),
            Codec = reader.IsDBNull(4) ? null : reader.GetString(4),
            Specials = reader.IsDBNull(5) ? null : reader.GetInt64(5) != 0,
            Wishes = TagList.Read(reader.GetString(6)),
            Musts = TagList.Read(reader.GetString(7)),
            Forbidden = TagList.Read(reader.GetString(8)),
            EnglishOnly = reader.IsDBNull(9) ? null : reader.GetInt64(9) != 0,
        };
    }
}
