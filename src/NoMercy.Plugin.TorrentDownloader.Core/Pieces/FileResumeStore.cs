// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

/// <summary>
/// One small binary record per torrent, named for its info hash.
///
/// <para>
/// The record carries the info hash and piece count as well as the bitfield, so a
/// record can prove it belongs to the torrent asking for it. A file name alone
/// cannot: names collide, get copied between folders, and survive a torrent being
/// removed and a different one added.
/// </para>
/// </summary>
public sealed class FileResumeStore(string folder) : IResumeStore
{
    private static readonly byte[] Magic = "NMTR"u8.ToArray();
    private const byte Version = 1;
    private const int HashLength = 20;
    private const int HeaderLength = 4 + 1 + HashLength + 4;

    public async Task<Bitfield?> LoadAsync(TorrentMetadata metadata, CancellationToken ct)
    {
        string path = PathFor(metadata);

        if (!File.Exists(path))
            return null;

        byte[] record;

        try
        {
            record = await File.ReadAllBytesAsync(path, ct);
        }
        catch (IOException)
        {
            return null;
        }

        if (record.Length < HeaderLength)
            return null;

        if (!record.AsSpan(0, Magic.Length).SequenceEqual(Magic) || record[4] != Version)
            return null;

        if (!record.AsSpan(5, HashLength).SequenceEqual(metadata.InfoHash))
            return null;

        int pieceCount = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(5 + HashLength, 4));

        if (pieceCount != metadata.PieceCount)
            return null;

        try
        {
            return Bitfield.FromBytes(record.AsSpan(HeaderLength), pieceCount);
        }
        catch (ArgumentException)
        {
            // Truncated, padded, or otherwise not the bitfield it claims to be.
            return null;
        }
    }

    public async Task SaveAsync(TorrentMetadata metadata, Bitfield have, CancellationToken ct)
    {
        if (have.Length != metadata.PieceCount)
            throw new ArgumentException($"the torrent has {metadata.PieceCount} pieces, not {have.Length}", nameof(have));

        Directory.CreateDirectory(folder);

        byte[] bitfield = have.ToBytes();
        byte[] record = new byte[HeaderLength + bitfield.Length];

        Magic.CopyTo(record, 0);
        record[4] = Version;
        metadata.InfoHash.CopyTo(record, 5);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(5 + HashLength, 4), metadata.PieceCount);
        bitfield.CopyTo(record, HeaderLength);

        // Write beside the record and move it into place. A crash partway through a
        // direct write would leave a record that claims pieces the disk does not hold,
        // which is the one thing this file must never do.
        string path = PathFor(metadata);
        string temporary = path + ".writing";

        await using (FileStream stream = new(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await stream.WriteAsync(record, ct);
            await stream.FlushAsync(ct);
            stream.Flush(flushToDisk: true);
        }

        File.Move(temporary, path, overwrite: true);
    }

    private string PathFor(TorrentMetadata metadata) =>
        Path.Combine(folder, Convert.ToHexStringLower(metadata.InfoHash) + ".resume");
}
