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
        IReadOnlyList<StoredRow> rows = await database.QueryAsync(
            """
            SELECT quality, codec, specials, wishes, musts, forbidden, english_only
            FROM library_preferences WHERE library_id = $library;
            """,
            new Dictionary<string, object?> { ["$library"] = libraryId },
            ct);

        if (rows is not [StoredRow row])
        {
            return new LibraryPreferences(libraryId);
        }

        return new(libraryId)
        {
            Quality = row.TextOrNull("quality"),
            Codec = row.Text("codec"),
            Specials = row.Flag("specials"),
            Wishes = TagList.Read(row.Text("wishes")),
            Musts = TagList.Read(row.Text("musts")),
            Forbidden = TagList.Read(row.Text("forbidden")),
            EnglishOnly = row.Flag("english_only"),
        };
    }

    public async Task SaveAsync(LibraryPreferences preferences, CancellationToken ct)
    {
        await database.ExecuteAsync(
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
            """,
            new Dictionary<string, object?>
            {
                ["$library"] = preferences.LibraryId,
                ["$quality"] = preferences.Quality,
                ["$codec"] = preferences.Codec,
                ["$specials"] = preferences.Specials ? 1 : 0,
                ["$wishes"] = TagList.Write(preferences.Wishes),
                ["$musts"] = TagList.Write(preferences.Musts),
                ["$forbidden"] = TagList.Write(preferences.Forbidden),
                ["$english"] = preferences.EnglishOnly ? 1 : 0,
            },
            ct);
    }
}
