// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>
/// The only thing in this plugin that hands bytes to another peer.
///
/// <para>
/// It exists for one case: a private tracker the user deliberately added and
/// deliberately configured to seed, because a private account with a ratio of zero
/// stops working. Every other torrent - and that is the default and the whole public
/// path - gets nothing from here, and no setting can change that. The gate is
/// <see cref="TorrentOrigin"/>, not a flag.
/// </para>
///
/// <para>
/// A request is untrusted input that reaches a file read, so every field is bounded
/// before the offsets are used.
/// </para>
/// </summary>
public sealed class PieceServer(
    TorrentMetadata metadata,
    IPieceStore store,
    SwarmPolicy policy,
    TorrentOrigin origin,
    Bitfield have)
{
    /// <summary>No peer needs more than this in one message, and a bigger one is a peer deciding our memory use.</summary>
    private const int MaxRequestLength = 128 * 1024;

    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;

    public long UploadedBytes { get; private set; }

    /// <summary>What this torrent cost to fetch. The ratio is measured against it.</summary>
    public long DownloadedBytes { get; init; }

    public double Ratio => DownloadedBytes > 0 ? (double)UploadedBytes / DownloadedBytes : 0;

    public bool HasMetItsTarget => policy.ShouldStopSeeding(Ratio, DateTimeOffset.UtcNow - _startedAt);

    /// <summary>
    /// Whether this torrent will serve anything at all right now. Asked before a peer
    /// is unchoked: unchoking someone we are going to refuse wastes its slot and ours,
    /// and tells a stranger we hold a torrent we were never going to share.
    /// </summary>
    public bool CanUpload => policy.MayUpload(origin) && !HasMetItsTarget;

    /// <summary>Null means "send this peer nothing", which is the answer in every case but one.</summary>
    public async Task<PieceBlock?> ServeAsync(Request request, CancellationToken ct)
    {
        if (!policy.MayUpload(origin))
            return null;

        if (HasMetItsTarget)
            return null;

        if (!IsWithinBounds(request))
            return null;

        if (!have[request.PieceIndex])
            return null;

        // Held, but not whole. This plugin writes only the video files out of a torrent, so
        // a piece straddling the nfo beside them is on disk with a hole in it.
        if (!store.CanServe(request.PieceIndex))
            return null;

        byte[] piece = await store.ReadPieceAsync(request.PieceIndex, ct);

        // Re-check against what actually came back: a short read means the piece is not
        // really there, and serving zeroes would make us the peer that lies.
        if (request.Begin + request.Length > piece.Length)
            return null;

        byte[] block = piece.AsSpan(request.Begin, request.Length).ToArray();
        UploadedBytes += block.Length;

        return new PieceBlock(request.PieceIndex, request.Begin, block);
    }

    private bool IsWithinBounds(Request request)
    {
        if (request.PieceIndex < 0 || request.PieceIndex >= metadata.PieceCount)
            return false;

        if (request.Begin < 0 || request.Length <= 0 || request.Length > MaxRequestLength)
            return false;

        return request.Begin + request.Length <= metadata.LengthOfPiece(request.PieceIndex);
    }
}
