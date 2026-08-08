// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pieces;

public sealed class FilePieceStore(TorrentMetadata metadata, string rootFolder) : IPieceStore, IDisposable
{
    private readonly Dictionary<string, FileStream> _open = [];
    private bool _disposed;

    public async Task WritePieceAsync(int pieceIndex, ReadOnlyMemory<byte> piece, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        int expected = metadata.LengthOfPiece(pieceIndex);

        if (piece.Length != expected)
            throw new ArgumentException($"piece {pieceIndex} is {expected} bytes, not {piece.Length}", nameof(piece));

        int offset = 0;

        foreach (FileSegment segment in PieceLayout.Segments(metadata, pieceIndex))
        {
            FileStream stream = Open(segment.File);
            stream.Seek(segment.OffsetInFile, SeekOrigin.Begin);
            await stream.WriteAsync(piece.Slice(offset, segment.Length), ct);
            offset += segment.Length;
        }
    }

    public async Task<byte[]> ReadPieceAsync(int pieceIndex, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        byte[] piece = new byte[metadata.LengthOfPiece(pieceIndex)];
        int offset = 0;

        foreach (FileSegment segment in PieceLayout.Segments(metadata, pieceIndex))
        {
            FileStream stream = Open(segment.File);

            if (stream.Length <= segment.OffsetInFile)
            {
                // Nothing was ever written this far into the file. Leave the rest zeroed;
                // the verifier rejects the piece, which is the correct answer.
                break;
            }

            stream.Seek(segment.OffsetInFile, SeekOrigin.Begin);

            int read = await stream.ReadAtLeastAsync(
                piece.AsMemory(offset, segment.Length),
                segment.Length,
                throwOnEndOfStream: false,
                ct);

            offset += segment.Length;

            if (read < segment.Length)
                break;
        }

        return piece;
    }

    public async Task FlushAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (FileStream stream in _open.Values)
            await stream.FlushAsync(ct);

        // FlushAsync alone leaves the bytes in the OS cache. The resume invariant needs
        // them on the platter before the record is written, so force it.
        foreach (FileStream stream in _open.Values)
            stream.Flush(flushToDisk: true);
    }

    private FileStream Open(FileEntry file)
    {
        string path = Path.Combine([rootFolder, .. file.Path]);

        if (_open.TryGetValue(path, out FileStream? existing))
            return existing;

        string? directory = Path.GetDirectoryName(path);

        if (directory is not null)
            Directory.CreateDirectory(directory);

        // ReadWrite sharing, not Read. We hold write access, and on Windows a reader whose
        // share mode does not also permit writing is refused - which would mean nothing could
        // look at a file while it downloads, including the server's own scanner.
        FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
        _open[path] = stream;
        return stream;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        foreach (FileStream stream in _open.Values)
            stream.Dispose();

        _open.Clear();
    }
}
