// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Swarm;

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

public enum EngineState
{
    Downloading,

    /// <summary>Every piece is in and verified. The files are at <c>CompletedFolder</c>.</summary>
    Completed,

    /// <summary>Given up on. <c>FailureReason</c> says what actually happened.</summary>
    Failed,
}

public sealed record EngineTransfer
{
    public required string InfoHash { get; init; }
    public required EngineState State { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int Peers { get; init; }
    public string? FailureReason { get; init; }
    public string? CompletedFolder { get; init; }
}

public sealed record TorrentRequest
{
    /// <summary>A magnet URI or the URL of a <c>.torrent</c>.</summary>
    public required string Source { get; init; }

    public required string DestinationFolder { get; init; }

    /// <summary>Decides whether this torrent may ever upload. Public unless a configured private tracker says otherwise.</summary>
    public TorrentOrigin Origin { get; init; } = TorrentOrigin.Public;

    /// <summary>Every tracker the indexers named for this info hash, merged. A bigger swarm is a faster download.</summary>
    public IReadOnlyList<string> ExtraTrackers { get; init; } = [];
}

/// <summary>
/// The whole of the engine, as the orchestrator sees it.
///
/// <para>
/// Add something, take it away, ask what is happening. Nothing about peers, pieces,
/// encryption or DHT crosses this line - which is the point: the orchestrator decides
/// what to download and the engine decides how, and neither has to understand the
/// other to be tested.
/// </para>
///
/// <para>
/// Deliberately polled rather than event-driven. The plugin already runs on a
/// scheduled task, a poll is trivial to fake in a test, and an event that fires while
/// the store is mid-write is a race nobody needs.
/// </para>
/// </summary>
public interface ITorrentEngine
{
    /// <summary>Returns the info hash, which is how everything else refers to this download.</summary>
    Task<string> AddAsync(TorrentRequest request, CancellationToken ct);

    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct);

    Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct);
}
