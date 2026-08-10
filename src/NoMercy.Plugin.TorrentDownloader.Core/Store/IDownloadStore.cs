// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

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
    public required string Indexer { get; init; }
    public long SizeBytes { get; init; }
    public GrabState State { get; init; } = GrabState.Grabbed;
    public DateTimeOffset GrabbedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public string? FailureReason { get; init; }
}

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
/// A show the refresh decided to leave alone, so a page can offer to stop leaving it
/// alone.
///
/// <para>
/// Written by the refresh rather than worked out again by whoever renders it. The first
/// version had the page ask the library and read <c>HaveEpisodeCount</c>, which the host
/// reports as zero for shows that plainly have episodes: the page offered to follow Silo
/// while Silo's missing episodes sat in the queue above it. The refresh already walks the
/// episodes to make the decision, so the decision is what gets recorded.
/// </para>
/// </summary>
public sealed record UnstartedShow
{
    public required int ShowId { get; init; }
    public required string Title { get; init; }
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

    /// <summary>Replaces the list wholesale, like the wanted list: it is a conclusion, not an accumulation.</summary>
    Task RecordUnstartedShowsAsync(IReadOnlyList<UnstartedShow> shows, CancellationToken ct);

    Task<IReadOnlyList<UnstartedShow>> UnstartedShowsAsync(CancellationToken ct);

    Task<WantedEpisode?> FindWantedAsync(EpisodeKey key, CancellationToken ct);

    Task MarkSearchedAsync(EpisodeKey key, DateTimeOffset when, WantedState state, CancellationToken ct);

    Task AddGrabAsync(Grab grab, CancellationToken ct);

    Task<Grab?> FindGrabAsync(string infoHash, CancellationToken ct);

    Task<IReadOnlyList<Grab>> ActiveGrabsAsync(CancellationToken ct);

    Task UpdateGrabAsync(string infoHash, GrabState state, string? failureReason, DateTimeOffset? finishedAt, CancellationToken ct);

    Task RecordTransferAsync(Transfer transfer, CancellationToken ct);

    Task<IReadOnlyList<Transfer>> TransfersAsync(CancellationToken ct);

    Task BlacklistAsync(BlacklistEntry entry, CancellationToken ct);

    /// <summary>True when this release should be skipped right now. Expired entries do not count.</summary>
    Task<bool> IsBlacklistedAsync(string? infoHash, string releaseTitle, DateTimeOffset now, CancellationToken ct);
}
