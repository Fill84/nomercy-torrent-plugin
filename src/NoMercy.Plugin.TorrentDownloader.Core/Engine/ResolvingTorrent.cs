// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

/// <summary>
/// A magnet the engine has taken on and cannot start yet.
///
/// <para>
/// It exists so that "we are working on this" is a thing the engine can say. Before it,
/// <c>AddAsync</c> blocked until BEP 9 answered and threw when it did not - which unwound
/// the caller's whole cycle and left no record anywhere that the torrent had ever been
/// chosen. On a real server that was a fortnight of a plugin deciding to download something
/// every five minutes and showing nothing for it.
/// </para>
/// </summary>
internal sealed record ResolvingTorrent
{
    public required string InfoHash { get; init; }
    public required TorrentRequest Request { get; init; }
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>
    /// Set when resolution gave up. The torrent stays listed rather than disappearing,
    /// because the reason is the only thing anybody can act on.
    /// </summary>
    public string? FailureReason { get; set; }
}
