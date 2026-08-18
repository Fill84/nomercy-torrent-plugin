namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What one file looked like when the resume was written.
/// </summary>
/// <param name="Path">Its path inside the torrent.</param>
/// <param name="Length">How many bytes it was.</param>
/// <param name="ModifiedUtc">
/// When it was last written, to the second. Anything finer is not kept across
/// every file system this could land on, and a resume that disagreed with the
/// disk over a microsecond would re-verify a six-gigabyte torrent for nothing.
/// </param>
public sealed record ResumeFile(string Path, long Length, DateTimeOffset ModifiedUtc);

/// <summary>
/// What was verified last time, so that a restart does not verify it again.
/// </summary>
/// <remarks>
/// <para>
/// Written on a clean stop and every <c>ResumeInterval</c>. A crash therefore
/// costs one interval of verification rather than the whole torrent — which for
/// a six-gigabyte file on a spinning disk is the difference between seconds and
/// several minutes of the server doing nothing else.
/// </para>
/// <para>
/// It is a cache and is treated as one: anything about it that does not match
/// the disk is thrown away rather than believed. The bytes on disk are the
/// truth and this file is only a claim about them.
/// </para>
/// </remarks>
public sealed record ResumeData(
    string InfoHash,
    Bitfield Verified,
    long Uploaded,
    long Downloaded,
    IReadOnlyList<ResumeFile> Files)
{
    /// <summary>What the file is called, per torrent.</summary>
    public static string FileName(string infoHash)
    {
        return $"{infoHash.ToUpperInvariant()}.resume";
    }

    /// <summary>The bytes to write.</summary>
    public byte[] Write()
    {
        return Bencode.Write(new BencodeDictionary(
        [
            new("info_hash"u8.ToArray(), new BencodeBytes(System.Text.Encoding.ASCII.GetBytes(InfoHash))),
            new("pieces"u8.ToArray(), new BencodeInteger(Verified.Pieces)),
            new("bitfield"u8.ToArray(), new BencodeBytes(Verified.Write())),
            new("uploaded"u8.ToArray(), new BencodeInteger(Uploaded)),
            new("downloaded"u8.ToArray(), new BencodeInteger(Downloaded)),
            new("files"u8.ToArray(), new BencodeList(
            [
                .. Files.Select(one => new BencodeDictionary(
                [
                    new("path"u8.ToArray(), new BencodeBytes(System.Text.Encoding.UTF8.GetBytes(one.Path))),
                    new("length"u8.ToArray(), new BencodeInteger(one.Length)),

                    // Seconds since the epoch, which is the same number on every
                    // machine this could be moved to.
                    new("modified"u8.ToArray(), new BencodeInteger(one.ModifiedUtc.ToUnixTimeSeconds())),
                ])),
            ])),
        ]));
    }

    /// <summary>
    /// Reads one, or null when there is nothing readable there.
    /// </summary>
    /// <remarks>
    /// Null rather than an exception: a resume file half written by a machine
    /// that lost power is the ordinary case this exists for, and the answer to
    /// it is to verify the torrent rather than to fail to start.
    /// </remarks>
    public static ResumeData? Read(ReadOnlySpan<byte> stored)
    {
        if (!Bencode.TryRead(stored, out BencodeDocument? document, out BencodeError? _)
            || document!.Root is not BencodeDictionary root
            || root.Text("info_hash") is not string infoHash
            || root.Number("pieces") is not long pieces
            || root.Bytes("bitfield") is not byte[] bits)
        {
            return null;
        }

        Bitfield verified;

        try
        {
            verified = Bitfield.Read(bits, (int)pieces);
        }
        catch (PeerProtocolException)
        {
            // A bitfield that is not the length it claims is a file that was
            // being written when the power went.
            return null;
        }

        List<ResumeFile> files = [];

        foreach (BencodeDictionary file in (root["files"] as BencodeList)?.Items.OfType<BencodeDictionary>() ?? [])
        {
            if (file.Text("path") is string path && file.Number("length") is long length && file.Number("modified") is long modified)
            {
                files.Add(new(path, length, DateTimeOffset.FromUnixTimeSeconds(modified)));
            }
        }

        return new(infoHash, verified, root.Number("uploaded") ?? 0, root.Number("downloaded") ?? 0, files);
    }

    /// <summary>
    /// What may still be believed, given what the files look like now.
    /// </summary>
    /// <remarks>
    /// A file whose size or modification time has changed has been touched by
    /// something that is not this client, and every piece covering any part of
    /// it goes back to unverified — including the pieces it shares with the
    /// file either side of it, because a piece is one hash over bytes from
    /// both.
    /// </remarks>
    /// <param name="torrent">The torrent, for where each file sits in the stream.</param>
    /// <param name="onDisk">What the files look like now, by path.</param>
    public Bitfield Trust(TorrentMetadata torrent, IReadOnlyDictionary<string, ResumeFile> onDisk)
    {
        Bitfield trusted = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            if (Verified.Has(piece))
            {
                trusted.Set(piece);
            }
        }

        foreach (TorrentFileEntry entry in torrent.Files)
        {
            ResumeFile? was = Files.FirstOrDefault(one => one.Path == entry.Path);

            if (was is not null && onDisk.TryGetValue(entry.Path, out ResumeFile? now) && Same(was, now))
            {
                continue;
            }

            // Changed, missing, or never recorded. Every piece that touches it
            // is suspect.
            foreach (int piece in Covering(torrent, entry))
            {
                trusted.Clear(piece);
            }
        }

        return trusted;
    }

    /// <summary>Every piece that any part of this file falls in.</summary>
    public static IEnumerable<int> Covering(TorrentMetadata torrent, TorrentFileEntry file)
    {
        if (file.Length <= 0)
        {
            yield break;
        }

        long first = file.Offset / torrent.PieceLength;
        long last = (file.Offset + file.Length - 1) / torrent.PieceLength;

        for (long piece = first; piece <= last && piece < torrent.PieceCount; piece++)
        {
            yield return (int)piece;
        }
    }

    private static bool Same(ResumeFile was, ResumeFile now)
    {
        return was.Length == now.Length && was.ModifiedUtc == now.ModifiedUtc;
    }
}
