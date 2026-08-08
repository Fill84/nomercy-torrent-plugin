// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public interface IResumeStore
{
    /// <summary>
    /// What the last save recorded, or null if there is nothing usable. Anything
    /// unreadable, truncated, or belonging to another torrent counts as nothing:
    /// re-downloading is cheap, and acting on a record that does not match is not.
    /// </summary>
    Task<Bitfield?> LoadAsync(TorrentMetadata metadata, CancellationToken ct);

    /// <summary>
    /// Call only after <see cref="IPieceStore.FlushAsync"/> has returned. The record
    /// must never claim more than the disk holds.
    /// </summary>
    Task SaveAsync(TorrentMetadata metadata, Bitfield have, CancellationToken ct);
}
