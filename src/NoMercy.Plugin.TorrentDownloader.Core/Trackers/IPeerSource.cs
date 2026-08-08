// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Core.Trackers;

public readonly record struct PeerEndPoint(IPAddress Address, int Port)
{
    public override string ToString() => $"{Address}:{Port}";
}

public enum AnnounceEvent
{
    /// <summary>A periodic re-announce. Carries no event field.</summary>
    None,
    Started,
    Completed,
    Stopped,
}

public sealed record AnnounceRequest(
    byte[] InfoHash,
    byte[] PeerId,
    int Port,
    long Downloaded,
    long Uploaded,
    long Left,
    AnnounceEvent Event);

public sealed record AnnounceResult(IReadOnlyList<PeerEndPoint> Peers, TimeSpan Interval);

/// <summary>
/// Somewhere peers come from. Three implementations end up here: HTTP trackers now,
/// UDP trackers and DHT in part two. The coordinator asks all of them and does not
/// care which one produced a given address.
/// </summary>
public interface IPeerSource
{
    /// <summary>
    /// Whether this source can announce to that address. Asked rather than inferred from
    /// the implementing type: an engine that decides by type check cannot be handed a
    /// different source, which makes it untestable and closes the door on the next one.
    /// </summary>
    bool CanAnnounceTo(string url);

    Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct);
}

public sealed class TrackerException(string message) : Exception(message);
