// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>
/// Where a torrent came from, which is the only thing that can permit uploading.
/// </summary>
public enum TorrentOrigin
{
    /// <summary>A public tracker or DHT. Never uploads, under any setting.</summary>
    Public,

    /// <summary>A private tracker the user added but did not ask to seed on.</summary>
    PrivateWithoutSeeding,

    /// <summary>A private tracker the user configured to seed, with a target.</summary>
    PrivateSeeding,
}

/// <summary>
/// The decisions that are policy rather than mechanism: how many peers, whether
/// uploading is allowed at all, when to stop.
///
/// <para>
/// Held apart from the coordinator so every one of them can be tested without
/// opening a socket, and changed without touching the code that moves bytes.
/// </para>
/// </summary>
public sealed record SwarmPolicy
{
    public static SwarmPolicy Default { get; } = new();

    /// <summary>The ceiling on live connections. The coordinator model does not degrade the way a shared lock does, so this is set for reach rather than for comfort.</summary>
    public int MaxConnectionsPerTorrent { get; init; } = 100;

    /// <summary>Dials in flight. Too many at once and a home router's NAT table suffers, which slows everything including playback.</summary>
    public int MaxHalfOpenConnections { get; init; } = 20;

    /// <summary>Long enough that a slow swarm is not abandoned, short enough that a dead release does not park the queue.</summary>
    public TimeSpan NoPeersTimeout { get; init; } = TimeSpan.FromMinutes(30);

    /// <summary>A swarm with peers answers a metadata request in seconds. This is the give-up point.</summary>
    public TimeSpan MetadataTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public int MaxPieceFailuresPerPeer { get; init; } = 3;

    /// <summary>The fraction of pieces still outstanding at which the tail is requested from several peers at once.</summary>
    public double EndgameThreshold { get; init; } = 0.05;

    public double SeedRatioTarget { get; init; } = 1.0;

    public TimeSpan SeedTimeTarget { get; init; } = TimeSpan.FromHours(72);

    /// <summary>
    /// The gate on <c>PieceServer</c>. A public torrent has no path to uploading at
    /// all - this is a switch on origin, not a default that a setting could flip.
    /// </summary>
    public bool MayUpload(TorrentOrigin origin) => origin == TorrentOrigin.PrivateSeeding;

    public bool HasRoomForAnotherPeer(int connected) => connected < MaxConnectionsPerTorrent;

    public bool MayDialAnother(int halfOpen) => halfOpen < MaxHalfOpenConnections;

    public bool ShouldBan(int pieceFailures) => pieceFailures >= MaxPieceFailuresPerPeer;

    /// <summary>
    /// Endgame is for a tail sitting with slow peers. Nothing outstanding is not a tail,
    /// it is a finished download - and on a small torrent the threshold rounds away to
    /// nothing, so any remaining piece counts.
    /// </summary>
    public bool ShouldEnterEndgame(int remaining, int total)
    {
        if (remaining <= 0)
            return false;

        return remaining <= Math.Max(1, (int)(total * EndgameThreshold));
    }

    public bool ShouldStopSeeding(double ratio, TimeSpan elapsed) =>
        ratio >= SeedRatioTarget || elapsed >= SeedTimeTarget;
}
