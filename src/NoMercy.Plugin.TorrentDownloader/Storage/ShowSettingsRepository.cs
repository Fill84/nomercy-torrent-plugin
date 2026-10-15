using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>The settings the owner saved per show, in <c>show_settings</c>.</summary>
/// <remarks>
/// A show with no row is off and follows its library in everything (<c>docs/specs/show-list.md</c>), which is why
/// <see cref="ForAsync"/> answers a fresh <see cref="ShowSettings"/> rather than nothing: every show
/// starts there, and a caller that had to tell "no row" from "off" would be one more place to get that
/// wrong.
/// </remarks>
public sealed class ShowSettingsRepository(Store database)
{
    private const string Columns = "show_id, switched_on, quality, codec, specials, wishes, musts, forbidden, english_only";

    public async Task<ShowSettings> ForAsync(int showId, CancellationToken ct)
    {
        IReadOnlyList<StoredRow> rows = await database.QueryAsync(
            $"SELECT {Columns} FROM show_settings WHERE show_id = $show;",
            new Dictionary<string, object?> { ["$show"] = showId },
            ct);

        return rows is [StoredRow row] ? Read(row) : new ShowSettings(showId);
    }

    /// <summary>Every show the owner has switched or set something on, by show id.</summary>
    public async Task<IReadOnlyDictionary<int, ShowSettings>> AllAsync(CancellationToken ct)
    {
        Dictionary<int, ShowSettings> all = [];

        foreach (StoredRow row in await database.QueryAsync($"SELECT {Columns} FROM show_settings;", null, ct))
        {
            ShowSettings one = Read(row);
            all[one.ShowId] = one;
        }

        return all;
    }

    /// <summary>Writes the settings form for one show: what the owner set on the show itself.</summary>
    public async Task SaveAsync(ShowSettings settings, CancellationToken ct)
    {
        await database.ExecuteAsync(
            """
            INSERT INTO show_settings (show_id, switched_on, quality, codec, specials, wishes, musts, forbidden, english_only)
            VALUES ($show, $on, $quality, $codec, $specials, $wishes, $musts, $forbidden, $english)
            ON CONFLICT(show_id) DO UPDATE SET
                switched_on = excluded.switched_on,
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
                ["$show"] = settings.ShowId,
                ["$on"] = settings.SwitchedOn ? 1 : 0,
                ["$quality"] = settings.Quality,
                ["$codec"] = settings.Codec,
                ["$specials"] = settings.Specials is bool specials ? (specials ? 1 : 0) : null,
                ["$wishes"] = TagList.Write(settings.Wishes),
                ["$musts"] = TagList.Write(settings.Musts),
                ["$forbidden"] = TagList.Write(settings.Forbidden),
                ["$english"] = settings.EnglishOnly is bool english ? (english ? 1 : 0) : null,
            },
            ct);
    }

    /// <summary>Switches a show on or off from the overview's row button.</summary>
    /// <remarks>
    /// Touches nothing else. A show switched off keeps what was set on it, and a show switched on
    /// follows its library in everything it has not set itself.
    /// </remarks>
    public async Task SwitchAsync(int showId, bool on, CancellationToken ct)
    {
        await database.ExecuteAsync(
            """
            INSERT INTO show_settings (show_id, switched_on) VALUES ($show, $on)
            ON CONFLICT(show_id) DO UPDATE SET switched_on = excluded.switched_on;
            """,
            new Dictionary<string, object?> { ["$show"] = showId, ["$on"] = on ? 1 : 0 },
            ct);
    }

    private static ShowSettings Read(StoredRow row)
    {
        return new(row.Int("show_id"))
        {
            SwitchedOn = row.Flag("switched_on"),
            Quality = row.TextOrNull("quality"),
            Codec = row.TextOrNull("codec"),
            Specials = row.FlagOrNull("specials"),
            Wishes = TagList.Read(row.Text("wishes")),
            Musts = TagList.Read(row.Text("musts")),
            Forbidden = TagList.Read(row.Text("forbidden")),
            EnglishOnly = row.FlagOrNull("english_only"),
        };
    }
}
