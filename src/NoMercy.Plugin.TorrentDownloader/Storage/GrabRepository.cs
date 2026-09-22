using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>One line of the history, as the table holds it.</summary>
/// <param name="Event">grabbed, decided, failed or dispatched.</param>
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
public sealed class GrabRepository(Store database)
{
    /// <summary>Records a grab, with every episode it answers for.</summary>
    /// <remarks>
    /// The second recording of a torrent <em>already open</em> is dropped, and
    /// the unique index on the hash is what decides that. A grab is recorded
    /// for each episode the cycle decided, so a pass that decides the same
    /// episode twice used to write the hash twice — and every step that walked
    /// grabs then walked both. The first row wins: its <c>grabbed_at</c> is
    /// when the torrent was really taken on, and the covers it carries are the
    /// whole of what that release answers for.
    ///
    /// A hash that <em>failed</em> is taken on again instead. Its
    /// row stays in the table and is hidden from the Downloads page, so
    /// dropping the insert against it left the owner looking at nothing
    /// grabbed, pasting the magnet by hand, and being refused by a row they
    /// could not see.
    ///
    /// And so is a hash that is <em>done</em>: the owner's decision of 11
    /// September 2026. A run only takes a torrent for an episode the library
    /// does not have, so a delivered torrent taken again is one whose episode
    /// never arrived — South Park S15E12, encoded on 1 September and filed by
    /// the server under another episode. Left done, the client was handed it
    /// and the next tick stopped it as nobody's. It is delivered again, from
    /// the beginning: nothing staged and no encode against it. The one other
    /// way in is the owner pasting that torrent by hand, which is asking for
    /// exactly the same thing.
    /// </remarks>
    public async Task RecordAsync(
        EpisodeKey episode,
        string showTitle,
        string releaseTitle,
        string source,
        string? infoHash,
        string? magnet,
        IReadOnlyList<EpisodeKey> covers,
        DateTimeOffset at,
        CancellationToken ct,
        string? folder = null)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO grabs (show_id, season, episode, release_title, info_hash, source, magnet, grabbed_at, state, covers, folder)
            VALUES ($show, $season, $episode, $release, $hash, $source, $magnet, $at, $state, $covers, $folder)
            ON CONFLICT (info_hash) DO UPDATE SET
                release_title = excluded.release_title,
                source        = excluded.source,
                magnet        = excluded.magnet,
                grabbed_at    = excluded.grabbed_at,
                state         = excluded.state,
                covers        = excluded.covers,
                folder        = excluded.folder,
                staged_path   = NULL
            -- Failed or done, never open: a grab still running is the first
            -- recording of this torrent and it wins. Done is taken on again
            -- only because a run takes a torrent for an episode the library
            -- does not have — the owner's decision of 11 September 2026. What
            -- this used to guard against, a finished grab dragged back while
            -- its episode was in the library, cannot come through here: the
            -- run never decides an episode the library has. Lost is over in
            -- the same way: another copy finished first, and a later run that
            -- takes this one again has a reason to.
            WHERE grabs.state IN ('failed', 'done', 'lost');
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
        command.Parameters.AddWithValue("$folder", (object?)folder ?? DBNull.Value);

        _ = showTitle;

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Where a grab staged its episodes, as the column holds them.</summary>
    /// <remarks>
    /// Newline-separated, which is the one character neither platform allows in
    /// a path. A row written when only the first was kept has no newline in it
    /// and reads back as the single path it always was.
    /// </remarks>
    private static IReadOnlyList<string> Staged(string column)
    {
        return [.. column.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
    }

    /// <summary>Everything not finished with, as recovery needs it.</summary>
    public async Task<IReadOnlyList<StoredDownload>> OpenAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            SELECT info_hash, magnet, release_title, state, covers, staged_path, folder, encode_jobs FROM grabs
            WHERE info_hash IS NOT NULL AND state NOT IN ('done', 'failed', 'lost');
            """;

        List<StoredDownload> open = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            open.Add(Row(reader, out GrabState _));
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
            SELECT info_hash, magnet, release_title, state, covers, staged_path, folder, encode_jobs FROM grabs
            WHERE info_hash IS NOT NULL;
            """;

        List<StoredDownload> all = [];

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            all.Add(Row(reader, out GrabState _));
        }

        return all;
    }

    /// <summary>One grab, as the two queries above select it.</summary>
    /// <remarks>
    /// One reader for both, so a column added to the row is added to every
    /// answer: the encode jobs were read by one query and not the other for a
    /// morning, and the sweep — which reads all of them — took a staged file the
    /// other query knew was still being encoded.
    /// </remarks>
    private static StoredDownload Row(SqliteDataReader reader, out GrabState state)
    {
        state = Enum.TryParse(reader.GetString(3), ignoreCase: true, out GrabState parsed) ? parsed : GrabState.Grabbed;

        return new(
            reader.GetString(0),
            reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
            reader.GetString(2),
            state)
        {
            Covers = Covered(reader.GetString(4)),
            StagedPaths = reader.IsDBNull(5) ? [] : Staged(reader.GetString(5)),
            Folder = reader.IsDBNull(6) ? null : reader.GetString(6),
            EncodeJobs = reader.IsDBNull(7) ? new Dictionary<EpisodeKey, string>() : Jobs(reader.GetString(7)),
        };
    }

    /// <summary>
    /// Writes down what the server called the encode it queued for one episode
    /// of a grab.
    /// </summary>
    /// <remarks>
    /// Appended, never replaced: a pack's episodes are dispatched one after
    /// another and each has a job of its own. An episode asked for twice keeps
    /// the later id, because that is the job the server is running.
    /// </remarks>
    public async Task EncodeAsync(string infoHash, EpisodeKey episode, string jobId, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand read = connection.CreateCommand();

        read.CommandText = "SELECT encode_jobs FROM grabs WHERE info_hash = $hash;";
        read.Parameters.AddWithValue("$hash", infoHash.ToUpperInvariant());

        Dictionary<EpisodeKey, string> jobs = await read.ExecuteScalarAsync(ct) is string held
            ? new(Jobs(held))
            : [];

        jobs[episode] = jobId;

        await using SqliteCommand write = connection.CreateCommand();

        write.CommandText = "UPDATE grabs SET encode_jobs = $jobs WHERE info_hash = $hash;";
        write.Parameters.AddWithValue(
            "$jobs",
            string.Join(' ', jobs.Select(one => $"{Tag(one.Key)}:{one.Value}")));
        write.Parameters.AddWithValue("$hash", infoHash.ToUpperInvariant());

        await write.ExecuteNonQueryAsync(ct);
    }

    /// <summary>The encode jobs of a grab, as the column holds them.</summary>
    /// <remarks>
    /// Space-separated <c>showXseasonXnumber:job</c>, the shape the column had
    /// the first time it existed (005). A part that does not read as one is
    /// skipped rather than thrown over: a column this plugin cannot read must
    /// not stop a grab from being recovered at all.
    /// </remarks>
    private static IReadOnlyDictionary<EpisodeKey, string> Jobs(string column)
    {
        Dictionary<EpisodeKey, string> jobs = [];

        foreach (string part in column.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0 || colon == part.Length - 1)
            {
                continue;
            }

            string[] numbers = part[..colon].Split('x');

            if (numbers.Length == 3
                && int.TryParse(numbers[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int show)
                && int.TryParse(numbers[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int season)
                && int.TryParse(numbers[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                jobs[new(show, season, number)] = part[(colon + 1)..];
            }
        }

        return jobs;
    }

    /// <summary>Writes down which episodes a grab turned out to answer for.</summary>
    /// <remarks>
    /// For a torrent added by hand, which is recorded covering no episode
    /// because claiming one nobody chose would put that episode back to missing
    /// if the download failed. Once it is finished, its own files say which
    /// episodes it holds — and every step after staging reads them from here,
    /// so working them out without writing them down would leave the dispatch
    /// done and the episodes still counted as missing.
    ///
    /// It refuses a grab that already covers something. A grab from the search
    /// chain answers for the episode the chain chose, and overwriting that with
    /// what the files happen to be named is how a release lands under the wrong
    /// episode.
    /// </remarks>
    public async Task CoversAsync(string infoHash, IReadOnlyList<EpisodeKey> covers, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            UPDATE grabs SET covers = $covers
            WHERE info_hash = $hash AND (covers IS NULL OR covers = '[]');
            """;

        command.Parameters.AddWithValue(
            "$covers",
            JsonSerializer.Serialize(covers.Select(one => new[] { one.ShowId, one.Season, one.Number })));

        command.Parameters.AddWithValue("$hash", infoHash.ToUpperInvariant());

        await command.ExecuteNonQueryAsync(ct);
    }

    /// <summary>How an episode is spelled inside the encode-jobs column.</summary>
    public static string Tag(EpisodeKey episode)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{episode.ShowId}x{episode.Season}x{episode.Number}");
    }

    /// <summary>Writes one line into the history and touches nothing else.</summary>
    /// <remarks>
    /// For something the owner has to be told and the plugin must not act on:
    /// a torrent added by hand that names no show they have. Failing the grab
    /// would throw the download away, and the answer changes the day the show
    /// is added; the Skipped page is wrong for it too, because that page is the
    /// release names a show's settings refused, each for an episode, and this one
    /// has no episode to be refused for.
    /// </remarks>
    public async Task NotedAsync(string releaseTitle, string reason, DateTimeOffset at, CancellationToken ct)
    {
        await using SqliteConnection connection = await database.OpenAsync(ct);
        await using SqliteCommand command = connection.CreateCommand();

        command.CommandText =
            """
            INSERT INTO history (at, event, show_id, season, episode, show_title, release_title, source, detail)
            VALUES ($at, 'failed', NULL, NULL, NULL, NULL, $release, NULL, $detail);
            """;

        command.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$release", releaseTitle);
        command.Parameters.AddWithValue("$detail", reason);

        await command.ExecuteNonQueryAsync(ct);
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
    public async Task StagedAsync(string infoHash, IReadOnlyList<string> paths, CancellationToken ct)
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

        // Newline-separated, because that is the one character a path on
        // neither platform can hold. A row written by a version that kept only
        // the first path reads back as the one path it has, which is what it
        // always meant.
        command.Parameters.AddWithValue("$path", string.Join('\n', paths));
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
    /// <para>
    /// <paramref name="until"/> is when the refusal runs out, and null is for
    /// ever. Every failure used to be for ever, whatever it was about: a swarm
    /// that did not answer on one evening refused that release permanently, and
    /// on 31 August 2026 that was South Park S15E12 1080p HMAX CtrlHD — fifty
    /// seeders on the site, blacklisted since 25 August, and the owner watching
    /// the plugin settle for a 720p.
    /// </para>
    /// </remarks>
    public async Task<int> FailedAsync(
        string infoHash,
        string reason,
        DateTimeOffset at,
        DateTimeOffset? until,
        CancellationToken ct)
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
                INSERT INTO blacklist (key, reason, at, until) VALUES ($key, $reason, $at, $until)
                ON CONFLICT(key) DO UPDATE SET reason = $reason, at = $at, until = $until;
                """;
            refusing.Parameters.AddWithValue("$key", hash);
            refusing.Parameters.AddWithValue("$reason", reason);
            refusing.Parameters.AddWithValue("$at", at.ToString("O", CultureInfo.InvariantCulture));
            refusing.Parameters.AddWithValue(
                "$until",
                (object?)until?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);

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
    /// Records that a file's encode was queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The history is what the owner reads to answer "what happened to that
    /// episode". A grab that reached the encoder and one that stopped at the
    /// intake folder look identical from the outside, and this line is the
    /// difference.
    /// </para>
    /// <para>
    /// <strong>No episode is a case, not a mistake.</strong> A file handed to a
    /// library for the server to identify has none — that is the whole reason
    /// it was handed over — and the line then names the file. Passing a key of
    /// noughts instead wrote ten rows reading "Series S00E00" for one pack, each
    /// saying nothing about which file it was for.
    /// </para>
    /// <para>
    /// <paramref name="library"/> is the name the owner gave it. A Ulid is what
    /// the server keys a library by and is not a thing a person can read.
    /// </para>
    /// </remarks>
    public async Task DispatchedAsync(
        EpisodeKey? episode,
        string? showTitle,
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
        command.Parameters.AddWithValue("$show", (object?)episode?.ShowId ?? DBNull.Value);
        command.Parameters.AddWithValue("$season", (object?)episode?.Season ?? DBNull.Value);
        command.Parameters.AddWithValue("$episode", (object?)episode?.Number ?? DBNull.Value);
        command.Parameters.AddWithValue("$title", (object?)showTitle ?? DBNull.Value);
        command.Parameters.AddWithValue("$release", releaseTitle);

        // Which of the two it was, in words: an episode the plugin named, or a
        // file the server was asked to work out for itself.
        command.Parameters.AddWithValue(
            "$detail",
            episode is null
                ? $"handed to {library} for the server to identify"
                : $"encode dispatched to {library}");

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

    /// <summary>
    /// How many lines of history a page is given.
    /// </summary>
    /// <remarks>
    /// Enough to see a night's work and few enough to draw. A refusal is
    /// written for every release every cycle considered and did not take, which
    /// on a library this size is thousands a day.
    /// </remarks>
    public const int Recent = 500;

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
            FROM history ORDER BY id DESC LIMIT $limit;
            """;

        // The newest, not all of them. The owner's history had 66,149 lines —
        // 65,878 of them refusals — and the page fetched every one, which is
        // why it stopped answering and nothing on it could be clicked.
        command.Parameters.AddWithValue("$limit", Recent);

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

    /// <summary>Every blacklisted key, read once for a run: a name or a hash carrying one is refused.</summary>
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
