// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.Json.Serialization;
using NoMercy.Plugin.TorrentDownloader.Core.Library;

namespace NoMercy.Plugin.TorrentDownloader.Core.Store;

/// <summary>Identifies one episode. The natural key everywhere in this plugin.</summary>
public readonly record struct EpisodeKey(int ShowId, int Season, int Episode)
{
    public override string ToString() => $"{ShowId} S{Season:D2}E{Episode:D2}";
}

public enum WantedState
{
    /// <summary>Missing, and nothing is being done about it yet.</summary>
    Wanted,

    Searching,

    /// <summary>A release was chosen and handed to the engine.</summary>
    Grabbed,

    Done,

    /// <summary>Searched enough times to conclude nobody is seeding it.</summary>
    Unavailable,
}

public sealed record WantedEpisode
{
    public required EpisodeKey Key { get; init; }
    public required string ShowTitle { get; init; }
    public string? EpisodeTitle { get; init; }
    public DateOnly? AirDate { get; init; }
    public WantedState State { get; init; } = WantedState.Wanted;
    public DateTimeOffset? LastSearchedAt { get; init; }
    public int SearchAttempts { get; init; }
}

public enum GrabState
{
    Grabbed,

    /// <summary>
    /// Handed to the engine, which is asking the swarm what this torrent actually contains.
    ///
    /// <para>
    /// No bytes yet, and none owed. A magnet names an info hash and nothing else, so until
    /// some peer answers there is no size to be a fraction of - which is why this is not
    /// Downloading with a zero in it.
    /// </para>
    /// </summary>
    Resolving,

    /// <summary>
    /// Stopped by the owner, and still theirs.
    ///
    /// <para>
    /// Written down rather than kept in the engine, which is where it used to live and
    /// only lived: the engine's paused set is in memory, so a restart forgot it, the
    /// transfers cadence saw an active grab the engine no longer knew, handed it straight
    /// back, and the download the owner had stopped quietly carried on.
    /// </para>
    ///
    /// <para>
    /// Still an active grab - the pages read that set to give a transfer its name, and a
    /// paused download has to stay nameable. It is not a running one, so it holds no
    /// download slot, exactly like a finished one waiting on its move.
    /// </para>
    /// </summary>
    Paused,

    Downloading,
    Downloaded,

    /// <summary>The file is in the intake. Written after the move, never before.</summary>
    Imported,

    Failed,
}

public sealed record Grab
{
    public required string InfoHash { get; init; }

    /// <summary>The episode whose turn in the queue caused this grab. Always one of <see cref="Covers"/>.</summary>
    public required EpisodeKey Key { get; init; }

    /// <summary>
    /// Every episode this torrent settles. One for an episode release; a season's worth
    /// for a pack.
    ///
    /// <para>
    /// Empty means "just <see cref="Key"/>", which is what every grab written before
    /// packs existed deserialises to - so an existing store file keeps meaning what it
    /// meant instead of needing a migration. Read it through <see cref="Covered"/>.
    /// </para>
    /// </summary>
    public IReadOnlyList<EpisodeKey> Covers { get; init; } = [];

    /// <summary>
    /// What this grab actually settles, with the empty case resolved.
    ///
    /// <para>
    /// Every caller wants this rather than <see cref="Covers"/>: marking only
    /// <see cref="Key"/> when a pack finishes is the bug this whole shape exists to
    /// stop, and it is the shape an unwary caller falls into.
    /// </para>
    /// </summary>
    public IReadOnlyList<EpisodeKey> Covered => Covers.Count == 0 ? [Key] : Covers;
    public required string ReleaseTitle { get; init; }

    /// <summary>
    /// The magnet or torrent URL this grab came from.
    ///
    /// <para>
    /// Kept so a download can be handed back to the engine after a restart. Without it the
    /// engine came up empty and every download in flight was stranded: the bytes were on
    /// disk, the grab said Downloaded, and nothing ever asked the engine about it again -
    /// so the import never ran and no encode was ever queued.
    /// </para>
    ///
    /// <para>
    /// Empty on a grab written before this existed. Those cannot be resumed and are left
    /// alone rather than guessed at.
    /// </para>
    /// </summary>
    public string Source { get; init; } = string.Empty;

    /// <summary>
    /// Where the engine put the bytes, written down the moment it says the download is
    /// complete.
    ///
    /// <para>
    /// A finished download does not need the engine any more - every piece is in and
    /// verified, and what is left is a file move. But the import used to be driven entirely
    /// off an engine transfer, so a grab that reached Downloaded and then failed its move
    /// could only be retried while the engine still remembered it. Two episodes were
    /// stranded exactly there: complete on disk, marked Downloaded, and grabbed before
    /// <see cref="Source"/> existed, so nothing could hand them back to the engine either.
    /// Unreachable, permanently, while holding two of the five download slots.
    /// </para>
    ///
    /// <para>
    /// Empty on a grab that has not finished, and on one written before this existed.
    /// </para>
    /// </summary>
    public string CompletedPath { get; init; } = string.Empty;

    public required string Indexer { get; init; }
    public long SizeBytes { get; init; }
    public GrabState State { get; init; } = GrabState.Grabbed;
    public DateTimeOffset GrabbedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? FailureReason { get; init; }
}

/// <summary>What became of a release, in the order it happened.</summary>
public enum HistoryEvent
{
    Grabbed,
    Imported,
    Failed,
    Cancelled,

    /// <summary>Found and wanted, but not taken. <c>Detail</c> says what stopped it.</summary>
    Skipped,
}

/// <summary>
/// One thing that happened, kept after the download itself is gone.
///
/// <para>
/// The pages before this showed only the present: what is downloading now, what is wanted
/// now. That is enough right up to the first morning an episode is missing and there is
/// nothing anywhere to say whether it was never found, grabbed and failed, or imported
/// into a library nobody was looking at. A queue answers "what next"; only a history
/// answers "what happened".
/// </para>
/// </summary>
public sealed record HistoryEntry
{
    public required DateTimeOffset At { get; init; }
    public required HistoryEvent Event { get; init; }
    public required EpisodeKey Key { get; init; }
    public required string ReleaseTitle { get; init; }
    public string? ShowTitle { get; init; }
    public string? Indexer { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Why, for the events that need a why. Null for the ones that do not.</summary>
    public string? Detail { get; init; }
}

/// <summary>
/// What one source last answered, so an owner can tell a quiet source from a broken one.
///
/// <para>
/// The page could only show what a source had produced <em>grabs</em> for, which makes
/// three very different sources look identical: one returning nothing, one returning forty
/// releases the profile turns down, and one answering 403 behind a Cloudflare check. On a
/// real server two of three sources had been the third for weeks and nothing said so.
/// </para>
/// </summary>
/// <param name="Released">How many releases came back. Zero with no failure is a source that answered, emptily.</param>
/// <param name="Failure">Why it did not answer, in the words the indexer used. Null when it did.</param>
public sealed record SourceReport(string Name, DateTimeOffset At, int Released, string? Failure);

public sealed record Transfer
{
    public required string InfoHash { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int Peers { get; init; }
    public long BytesPerSecond { get; init; }

    /// <summary>Whether the owner stopped this one. A paused transfer keeps its bar and stops claiming to be busy.</summary>
    public bool Paused { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public double Progress => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;

    /// <summary>
    /// How long the rest will take at the rate it is going, or null when that cannot
    /// honestly be said. A stalled torrent has no estimate, and "a very long time" is a
    /// worse answer than no answer.
    /// </summary>
    public TimeSpan? Remaining =>
        !Paused && BytesPerSecond > 0 && BytesTotal > BytesDone
            ? TimeSpan.FromSeconds((BytesTotal - BytesDone) / (double)BytesPerSecond)
            : null;
}

/// <summary>
/// A show the plugin is working on, and what the last refresh concluded about it.
///
/// <para>
/// This list is the plugin's answer to "which shows are yours". Everything the refresh
/// passed over - a library row with no episode on the server, a series that has ended -
/// is not in it and is not recorded anywhere else either. A show nobody has is not a
/// thing to show somebody a list of.
/// </para>
///
/// <para>
/// Recorded whether or not anything is missing from it. Only wanted episodes were kept
/// before, so a show that was up to date existed in no list the plugin held - and a running
/// series with a new episode due next week is exactly the show that is up to date most of
/// the time. It was invisible on every page until it fell behind.
/// </para>
/// </summary>
public sealed record TrackedShow
{
    public required int ShowId { get; init; }
    public required string Title { get; init; }

    /// <summary>
    /// Whether anything of it is on the server.
    ///
    /// <para>
    /// False only for a show the owner asked for by name before anything of it has
    /// arrived. Every other show in this list has at least one episode, because that is
    /// what got it in.
    /// </para>
    /// </summary>
    public required bool Started { get; init; }

    /// <summary>Where the library says the show stands. The one thing that decides whether more of it is coming.</summary>
    public required ShowStatus Status { get; init; }

    /// <summary>
    /// Whether more of it is ever coming.
    ///
    /// <para>
    /// Computed, not stored: it is a reading of <see cref="Status"/> and storing both
    /// invites a file where they disagree.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public bool Running => Status.StillGoing();

    /// <summary>When the next episode airs, when the library knows of one that has not yet.</summary>
    public DateOnly? NextAirDate { get; init; }
}

public sealed record BlacklistEntry
{
    /// <summary>Either identifies a bad release. Some sources give no hash, so a title has to do.</summary>
    public string? InfoHash { get; init; }

    public string? ReleaseTitle { get; init; }

    public required string Reason { get; init; }

    public DateTimeOffset AddedAt { get; init; }

    /// <summary>Null means forever. A release that failed once may be fine next month, so most entries expire.</summary>
    public DateTimeOffset? ExpiresAt { get; init; }

    /// <summary>
    /// A short, stable, URL-safe handle for this entry, so a button on the page can name
    /// which one it means.
    ///
    /// <para>
    /// Derived rather than stored: an index into a list reorders as entries expire, and a
    /// release title is an arbitrary string that can carry slashes straight through a
    /// route. Hashing the identity gives the same handle for the same entry on every
    /// render, and never a character a URL has to be told about.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public string Handle => Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(InfoHash ?? ReleaseTitle ?? Reason)))[..12].ToLowerInvariant();
}

/// <summary>
/// What the plugin remembers between runs.
///
/// <para>
/// It answers questions and records outcomes. It does not decide what happens next -
/// ordering and the in-flight limit belong to the orchestrator, so this stays a thing
/// that can be swapped for an in-memory one in every test that is not about SQL.
/// </para>
/// </summary>
public interface IDownloadStore
{
    /// <summary>
    /// Brings the wanted list in line with what the library actually holds. Episodes the
    /// library no longer misses stop being wanted; ones it now misses start. The library
    /// is the truth and this list is derived from it.
    /// </summary>
    Task RefreshWantedAsync(IReadOnlyList<WantedEpisode> missing, CancellationToken ct);

    Task<IReadOnlyList<WantedEpisode>> WantedAsync(int limit, CancellationToken ct);

    /// <summary>
    /// Every show the plugin is working on. Replaced wholesale, like the wanted list: it is
    /// a conclusion, not an accumulation, so a show that stops qualifying disappears on the
    /// next refresh rather than needing anyone to clear anything by hand.
    /// </summary>
    Task RecordShowsAsync(IReadOnlyList<TrackedShow> shows, CancellationToken ct);

    Task<IReadOnlyList<TrackedShow>> ShowsAsync(CancellationToken ct);

    Task<WantedEpisode?> FindWantedAsync(EpisodeKey key, CancellationToken ct);

    Task MarkSearchedAsync(EpisodeKey key, DateTimeOffset when, WantedState state, CancellationToken ct);

    Task AddGrabAsync(Grab grab, CancellationToken ct);

    Task<Grab?> FindGrabAsync(string infoHash, CancellationToken ct);

    Task<IReadOnlyList<Grab>> ActiveGrabsAsync(CancellationToken ct);

    Task UpdateGrabAsync(string infoHash, GrabState state, string? failureReason, DateTimeOffset? finishedAt, CancellationToken ct);

    /// <summary>
    /// Remembers where a finished download's bytes are, so the import no longer depends on
    /// the engine still holding the torrent. See <see cref="Grab.CompletedPath"/>.
    /// </summary>
    Task RecordCompletedPathAsync(string infoHash, string completedPath, CancellationToken ct);

    Task RecordTransferAsync(Transfer transfer, CancellationToken ct);

    /// <summary>Replaces what is known about the sources that just answered. See <see cref="SourceReport"/>.</summary>
    Task RecordSourceReportsAsync(IReadOnlyList<SourceReport> reports, CancellationToken ct);

    Task<IReadOnlyList<SourceReport>> SourceReportsAsync(CancellationToken ct);

    Task<IReadOnlyList<Transfer>> TransfersAsync(CancellationToken ct);

    Task RecordHistoryAsync(HistoryEntry entry, CancellationToken ct);

    /// <summary>The most recent first, capped at <paramref name="limit"/>.</summary>
    Task<IReadOnlyList<HistoryEntry>> HistoryAsync(int limit, CancellationToken ct);

    Task BlacklistAsync(BlacklistEntry entry, CancellationToken ct);

    /// <summary>True when this release should be skipped right now. Expired entries do not count.</summary>
    Task<bool> IsBlacklistedAsync(string? infoHash, string releaseTitle, DateTimeOffset now, CancellationToken ct);

    /// <summary>Everything currently being skipped, so the page can show it and offer to stop.</summary>
    Task<IReadOnlyList<BlacklistEntry>> BlacklistedAsync(DateTimeOffset now, CancellationToken ct);

    /// <summary>Lifts one skip, by its handle. False when there was nothing under that handle.</summary>
    Task<bool> AllowAgainAsync(string handle, CancellationToken ct);
}
