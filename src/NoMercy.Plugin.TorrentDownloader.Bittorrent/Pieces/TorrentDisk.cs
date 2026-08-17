using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The files of one torrent, as one byte stream.
/// </summary>
/// <remarks>
/// <para>
/// A piece is a range of that stream and pays no attention to where one file
/// ends and the next begins. Writing one is therefore one write per file it
/// touches, at the offset each of them starts — a client that wrote a piece to
/// a single file would corrupt two.
/// </para>
/// <para>
/// Files are made at their full size and sparse, so a torrent of six gigabytes
/// does not spend six gigabytes of writing before the first block arrives, and
/// so the disk is not filled by a download that is later cancelled.
/// </para>
/// </remarks>
public sealed class TorrentDisk(TorrentMetadata torrent, string folder) : IDisposable
{
    private readonly Dictionary<string, FileStream> _open = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();

    /// <summary>Where this torrent's files live.</summary>
    /// <remarks>
    /// A multi-file torrent goes in a folder of its own name; a single-file one
    /// is the file itself, and putting it in a folder anyway would have the
    /// staging step look for a name that is not there.
    /// </remarks>
    public string Root => torrent.Files.Count > 1 ? Path.Combine(folder, torrent.Name) : folder;

    /// <summary>Makes every file at full size, without writing its bytes.</summary>
    public void Create()
    {
        foreach (TorrentFileEntry file in torrent.Files)
        {
            string path = PathOf(file);

            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (File.Exists(path) && new FileInfo(path).Length == file.Length)
            {
                // Already the right size, so there is nothing to do. Setting
                // the length again would not lose anything — it is the same
                // number — but a torrent of twenty-three files is twenty-three
                // opens per resume for no reason.
                continue;
            }

            using FileStream stream = new(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);

            MarkSparse(stream);

            stream.SetLength(file.Length);
        }
    }

    /// <summary>
    /// Writes one piece, across as many files as it covers.
    /// </summary>
    public void Write(int piece, ReadOnlySpan<byte> bytes)
    {
        long at = (long)piece * torrent.PieceLength;

        foreach (TorrentSlice slice in torrent.Slice(at, bytes.Length))
        {
            FileStream stream = Open(slice.File);

            lock (_lock)
            {
                stream.Position = slice.OffsetInFile;
                stream.Write(bytes[..(int)slice.Length]);
                stream.Flush();
            }

            bytes = bytes[(int)slice.Length..];
        }
    }

    /// <summary>Reads a range back, for uploading and for verification.</summary>
    public byte[] Read(long offset, int length)
    {
        byte[] bytes = new byte[length];
        int at = 0;

        foreach (TorrentSlice slice in torrent.Slice(offset, length))
        {
            FileStream stream = Open(slice.File);

            lock (_lock)
            {
                stream.Position = slice.OffsetInFile;
                stream.ReadExactly(bytes.AsSpan(at, (int)slice.Length));
            }

            at += (int)slice.Length;
        }

        return bytes;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            foreach (FileStream stream in _open.Values)
            {
                stream.Dispose();
            }

            _open.Clear();
        }
    }

    /// <summary>Where one file of this torrent goes.</summary>
    public string PathOf(TorrentFileEntry file)
    {
        return Path.Combine(Root, file.Path);
    }

    private FileStream Open(TorrentFileEntry file)
    {
        lock (_lock)
        {
            if (!_open.TryGetValue(file.Path, out FileStream? stream))
            {
                stream = new(PathOf(file), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
                _open[file.Path] = stream;
            }

            return stream;
        }
    }

    /// <summary>
    /// Asks the file system not to reserve the bytes until they are written.
    /// </summary>
    /// <remarks>
    /// NTFS reserves the whole length when it is set, so a six-gigabyte torrent
    /// costs six gigabytes the moment it starts — before a single peer has
    /// answered. Every other file system this runs on is sparse by default, so
    /// there is nothing to ask.
    /// </remarks>
    private static void MarkSparse(FileStream stream)
    {
        if (OperatingSystem.IsWindows())
        {
            Sparse.Mark(stream);
        }
    }
}

/// <summary>Marking a file sparse on Windows, which has no managed way to do it.</summary>
[SupportedOSPlatform("windows")]
internal static class Sparse
{
    private const int FsctlSetSparse = 0x000900C4;

    public static void Mark(FileStream stream)
    {
        // Best effort. A file system that refuses — FAT32 on a USB disk, say —
        // still holds the download; it merely reserves the space up front, and
        // failing the torrent over it would be worse than the reservation.
        _ = DeviceIoControl(
            stream.SafeFileHandle,
            FsctlSetSparse,
            nint.Zero,
            0,
            nint.Zero,
            0,
            out _,
            nint.Zero);
    }

    // DllImport rather than LibraryImport: the generated marshalling code is
    // unsafe, and this assembly compiles without unsafe blocks — which is worth
    // more than the microseconds the generator would save on a call made once
    // per file.
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        Microsoft.Win32.SafeHandles.SafeFileHandle device,
        int code,
        nint input,
        int inputLength,
        nint output,
        int outputLength,
        out int returned,
        nint overlapped);
}
