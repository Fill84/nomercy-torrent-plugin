using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>One line of the history, as the table holds it.</summary>
/// <param name="Event">grabbed, skipped, failed, dispatched or allowed.</param>
/// <param name="At">When it happened.</param>
/// <param name="ShowId">Which show, when the line is about an episode.</param>
/// <param name="Season">Which season, when the line is about an episode.</param>
/// <param name="Number">Which episode, when the line is about an episode.</param>
/// <param name="ShowTitle">What the show is called.</param>
/// <param name="ReleaseTitle">What the release was called.</param>
/// <param name="Source">The site it came from, when one was named.</param>
/// <param name="Detail">The reason, the library, or whatever that event carries.</param>
public sealed record HistoryRow(
    string Event,
    DateTimeOffset At,
    int? ShowId,
    int? Season,
    int? Number,
    string? ShowTitle,
    string? ReleaseTitle,
    string? Source,
    string? Detail);

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
            SELECT info_hash, magnet, release_title, state, covers, staged_path FROM grabs
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
                Enum.TryParse(reader.GetString(3), ignoreCase: true, out GrabState state) ? state : GrabState.Grabbed)
            {
                Covers = Covered(reader.GetString(4)),
                StagedPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return open;
    }

    /// <summary>
    /// Every grab there has ever been, whatever became of it.
    /// </summary>
    /// <remarks>
    /// For matching a file in the intake folder back to what put it there. A
    /// grab that was marked done before it recorded where it staged its episode
    /// is finished as far as <see cref="OpenAsync"/> is concerned, and is the
    /// only thing that knows which show the file belongs to.
    /// </remarks>
    public async Task<IReadOnlyList<StoredDownload>> EveryAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT info_hash, magnet, release_title, state, covers, staged_path FROM grabs
            WHERE info_hash IS NOT NULL;
            """;

        List<StoredDownload> all = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            all.Add(new(
                reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetString(2),
                Enum.TryParse(reader.GetString(3), ignoreCase: true, out GrabState state) ? state : GrabState.Grabbed)
            {
                Covers = Covered(reader.GetString(4)),
                StagedPath = reader.IsDBNull(5) ? null : reader.GetString(5),
            });
        }

        return all;
    }

    /// <summary>
    /// Records that a grab's episode is now in the intake folder.
    /// </summary>
    /// <remarks>
    /// The path with the state, in one write. A grab that said it was staged
    /// without saying where would have the file looked for by name on every
    /// tick, and a grab that said where without saying so would be staged all
    /// over again.
    /// </remarks>
    public async Task StagedAsync(string infoHash, string path, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        // Done is not spared here, and it is the only write that does not spare
        // it. A grab marked done before it recorded where it staged its episode
        // was never really finished — the encode may never have been asked for
        // — and this is the deliberate correction of that. Every other write
        // leaves a finished grab alone, because there the danger is a later
        // failure dragging it back.
        command.CommandText =
            """
            UPDATE grabs SET state = 'staged', staged_path = $path
            WHERE info_hash = $hash;
            """;

        command.Parameters.AddWithValue("$path", path);
        command.Parameters.AddWithValue("$hash", infoHash.ToUpperInvariant());

        await command.ExecuteNonQueryAsync(ct);
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

        // Never a grab that is done. State is written by info hash and one
        // release could have several rows under one — so a later failure of the
        // same torrent used to drag the finished one back with it, put the
        // episode to missing and have it searched for again though its file was
        // already staged. It took the owner's finished grabs from twenty-three
        // to eleven overnight.
        command.CommandText = "UPDATE grabs SET state = $state WHERE info_hash = $hash AND state <> 'done';";
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
            marking.CommandText = "UPDATE grabs SET state = 'failed' WHERE info_hash = $hash AND state <> 'done';";
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

    /// <summary>
    /// Records that an episode's encode was queued.
    /// </summary>
    /// <remarks>
    /// The history is what the owner reads to answer "what happened to that
    /// episode". A grab that reached the encoder and one that stopped at the
    /// intake folder look identical from the outside, and this line is the
    /// difference.
    /// </remarks>
    public async Task DispatchedAsync(
        EpisodeKey episode,
        string showTitle,
        string releaseTitle,
        string library,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ($at, 'dispatched', $show, $season, $episode, $title, $release, NULL, $detail);
            """;

        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$title", showTitle);
        command.Parameters.AddWithValue("$release", releaseTitle);
        command.Parameters.AddWithValue("$detail", $"encode dispatched to library {library}");

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every episode a grab answers for, as the column holds them.</summary>
    private static IReadOnlyList<EpisodeKey> Covered(string json)
    {
        List<EpisodeKey> covered = [];

        foreach (int[] one in JsonSerializer.Deserialize<int[][]>(json) ?? [])
        {
            // Three, or it is not an episode. A row written by something that
            // did not agree about the shape is not one to guess at.
            if (one.Length == 3)
            {
                covered.Add(new(one[0], one[1], one[2]));
            }
        }

        return covered;
    }

    /// <summary>
    /// Records a release the profile or the blacklist refused, and why.
    /// </summary>
    /// <remarks>
    /// In the history rather than in a list held for the cycle, because the
    /// Skipped page is opened after the fact — usually the next morning, and
    /// usually because an episode did not arrive. A refusal that lived only in
    /// memory would be gone by then, and the page would say nothing was
    /// refused when something was.
    /// </remarks>
    public async Task RecordSkippedAsync(
        EpisodeKey episode,
        string showTitle,
        string releaseTitle,
        string? source,
        string reason,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ($at, 'skipped', $show, $season, $episode, $title, $release, $source, $reason);
            """;

        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$title", showTitle);
        command.Parameters.AddWithValue("$release", releaseTitle);
        command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("$reason", reason);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Records a release that was decided on and not handed over.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Dry run decides everything and hands nothing to the client, and until
    /// this existed it wrote down nothing at all — so a cycle that found the
    /// right release for every episode left a Skipped page full of refusals and
    /// no trace of a single thing it would have taken. The owner reads that as
    /// a plugin that refused everything, and they are reading the only evidence
    /// there was.
    /// </para>
    /// <para>
    /// A client that would not take the torrent lands here too. It is a
    /// decision that was made and not carried out, which is the same kind of
    /// line, and it carries the reason the client gave.
    /// </para>
    /// </remarks>
    public async Task RecordDecidedAsync(
        EpisodeKey episode,
        string showTitle,
        string releaseTitle,
        string? source,
        string detail,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ($at, 'decided', $show, $season, $episode, $title, $release, $source, $detail);
            """;

        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$title", showTitle);
        command.Parameters.AddWithValue("$release", releaseTitle);
        command.Parameters.AddWithValue("$source", (object?)source ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail);

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// A download the owner cancelled: forgotten, and its episodes put back.
    /// </summary>
    /// <remarks>
    /// Both, or the episode is lost — one left marked as grabbed with nothing
    /// downloading is one nothing will ever look for again. Nothing is
    /// blacklisted: the owner said no to this download, not to this release for
    /// ever, and refusing it on their behalf tomorrow is a decision they did
    /// not make.
    /// </remarks>
    /// <returns>Whether there was a grab to cancel.</returns>
    public async Task<bool> CancelledAsync(string infoHash, DateTimeOffset at, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct);

        string hash = infoHash.ToUpperInvariant();
        List<EpisodeKey> covered = [];
        string? release = null;

        await using (SqliteCommand reading = connection.CreateCommand())
        {
            reading.Transaction = transaction;
            reading.CommandText = "SELECT covers, release_title FROM grabs WHERE info_hash = $hash;";
            reading.Parameters.AddWithValue("$hash", hash);

            await using SqliteDataReader reader = await reading.ExecuteReaderAsync(ct);

            while (await reader.ReadAsync(ct))
            {
                covered.AddRange(Covered(reader.GetString(0)));
                release ??= reader.GetString(1);
            }
        }

        if (release is null)
        {
            return false;
        }

        await using (SqliteCommand forgetting = connection.CreateCommand())
        {
            forgetting.Transaction = transaction;

            // Deleted rather than marked: a cancelled grab is one that never
            // happened as far as every page is concerned, and a row left behind
            // would keep it on the Downloads page for ever.
            forgetting.CommandText = "DELETE FROM grabs WHERE info_hash = $hash;";
            forgetting.Parameters.AddWithValue("$hash", hash);

            await forgetting.ExecuteNonQueryAsync(ct);
        }

        foreach (EpisodeKey episode in covered)
        {
            await using SqliteCommand missing = connection.CreateCommand();

            missing.Transaction = transaction;

            // B2: a download the owner cancelled is not an attempt the episode
            // spent, so the attempt count is left alone.
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

        await using (SqliteCommand said = connection.CreateCommand())
        {
            said.Transaction = transaction;
            said.CommandText =
                """
                INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
                VALUES ($at, 'failed', $show, $season, $episode, NULL, $release, NULL, $detail);
                """;

            EpisodeKey first = covered.Count > 0 ? covered[0] : new(0, 0, 0);

            said.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            said.Parameters.AddWithValue("$show", first.ShowId);
            said.Parameters.AddWithValue("$season", first.Season);
            said.Parameters.AddWithValue("$episode", first.Number);
            said.Parameters.AddWithValue("$release", release);
            said.Parameters.AddWithValue("$detail", "cancelled by hand");

            await said.ExecuteNonQueryAsync(ct);
        }

        await transaction.CommitAsync(ct);

        return true;
    }

    /// <summary>Why one release was refused for one episode, or null when none was.</summary>
    /// <remarks>
    /// The newest refusal, because the profile can change between cycles and
    /// what the owner is overruling is the reason they were shown.
    /// </remarks>
    public async Task<string?> RefusalAsync(EpisodeKey episode, string title, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT detail FROM history
            WHERE event = 'skipped' AND show_id = $show AND season = $season AND episode = $episode
              AND release_title = $title
            ORDER BY id DESC LIMIT 1;
            """;

        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$title", title);

        object? detail = await command.ExecuteScalarAsync(ct);

        return detail is string reason ? reason : null;
    }

    /// <summary>Records that the owner overruled a refusal, and what it had been.</summary>
    /// <remarks>
    /// Naming the original reason is the whole of it. A line saying only
    /// "allowed" has the History page contradicting the Skipped page it came
    /// from, with nothing to say which of the two is right.
    /// </remarks>
    public async Task AllowedAsync(
        EpisodeKey episode,
        string releaseTitle,
        string refusedFor,
        DateTimeOffset at,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ($at, 'allowed', $show, $season, $episode, NULL, $release, NULL, $detail);
            """;

        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$show", episode.ShowId);
        command.Parameters.AddWithValue("$season", episode.Season);
        command.Parameters.AddWithValue("$episode", episode.Number);
        command.Parameters.AddWithValue("$release", releaseTitle);
        command.Parameters.AddWithValue("$detail", $"allowed by hand, having been refused: {refusedFor}");

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Every refusal, newest first, as the Skipped page reads them.</summary>
    public async Task<IReadOnlyList<SkippedRelease>> SkippedAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT show_id, season, episode, release_title, source, detail FROM history
            WHERE event = 'skipped' ORDER BY id DESC;
            """;

        List<SkippedRelease> refused = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            refused.Add(new(
                new(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2)),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),

                // Never blank. A refusal with no reason is the one thing the
                // owner opened the page to read, and an empty string there
                // would render as a row that refuses to say why.
                reader.IsDBNull(5) ? "no reason was recorded" : reader.GetString(5)));
        }

        return refused;
    }

    /// <summary>What the history says happened, newest first.</summary>
    /// <remarks>
    /// Every column, not the two the first caller wanted. The page says when a
    /// thing happened, which episode it was about and why, and a reader that
    /// answered only the event and the reason would have the page inventing the
    /// other two.
    /// </remarks>
    public async Task<IReadOnlyList<HistoryRow>> HistoryAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT event, at, show_id, season, episode, show_title, release_title, source, detail
            FROM history ORDER BY id DESC;
            """;

        List<HistoryRow> lines = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            lines.Add(new(
                reader.GetString(0),
                DateTimeOffset.Parse(reader.GetString(1), CultureInfo.InvariantCulture),
                reader.IsDBNull(2) ? null : reader.GetInt32(2),
                reader.IsDBNull(3) ? null : reader.GetInt32(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8)));
        }

        return lines;
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
