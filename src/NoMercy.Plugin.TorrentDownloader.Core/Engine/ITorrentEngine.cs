// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Swarm;

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

public enum EngineState
{
    /// <summary>
    /// Added, and waiting for a peer to hand over what the torrent actually contains.
    ///
    /// <para>
    /// Its own state rather than Downloading-at-nought-per-cent, because the two fail for
    /// different reasons and only one of them is worth a progress bar. A magnet names an
    /// info hash and nothing else; until some peer answers, the engine does not know how
    /// many bytes there are to be at nought per cent of.
    /// </para>
    /// </summary>
    Resolving,

    Downloading,

    /// <summary>Every piece is in and verified. The files are at <c>CompletedFolder</c>.</summary>
    Completed,

    /// <summary>Given up on. <c>FailureReason</c> says what actually happened.</summary>
    Failed,

    /// <summary>
    /// Stopped by the owner, and still theirs. The pieces already on disk are kept and a
    /// resume picks up from them - the same recovery a server restart uses, which is why
    /// pausing does not need a half-alive state of its own to go wrong in.
    /// </summary>
    Paused,
}

public sealed record EngineTransfer
{
    public required string InfoHash { get; init; }
    public required EngineState State { get; init; }
    public long BytesDone { get; init; }
    public long BytesTotal { get; init; }
    public int Peers { get; init; }

    /// <summary>
    /// How fast it is actually going, measured rather than averaged over the whole
    /// download. Percentage answers "how far"; this answers "is it moving", and on a
    /// torrent those are different questions - a stalled one sits at 34% all evening.
    /// </summary>
    public long BytesPerSecond { get; init; }

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

    /// <summary>
    /// Stops a torrent without forgetting it. The bytes stay, and a resume starts it again
    /// from what is on disk.
    /// </summary>
    Task PauseAsync(string infoHash, CancellationToken ct);

    /// <summary>Starts a paused torrent again. Does nothing to one that was never paused.</summary>
    Task ResumeAsync(string infoHash, CancellationToken ct);

    Task<IReadOnlyList<EngineTransfer>> TransfersAsync(CancellationToken ct);
}
