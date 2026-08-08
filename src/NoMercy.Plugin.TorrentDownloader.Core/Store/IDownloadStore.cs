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
    public required EpisodeKey Key { get; init; }
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
    public DateTimeOffset UpdatedAt { get; init; }

    public double Progress => BytesTotal > 0 ? (double)BytesDone / BytesTotal : 0;
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
