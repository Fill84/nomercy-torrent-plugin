namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// When resume data is written, and where.
/// </summary>
/// <remarks>
/// <para>
/// On a clean stop, and every <c>ResumeInterval</c> in between. The interval is
/// the whole design: writing after every verified piece would have the disk
/// busy with resume files instead of the download, and writing only on a clean
/// stop would mean a crash cost the entire torrent's verification.
/// </para>
/// <para>
/// Each file is written to a temporary name and moved into place. A resume file
/// that was half written when the power went is worse than none: it parses far
/// enough to be believed and then says pieces are verified that are not.
/// </para>
/// </remarks>
public sealed class ResumeKeeper(string folder, TimeSpan interval, TimeProvider time)
{
    private DateTimeOffset _last = DateTimeOffset.MinValue;

    /// <summary>Where the files live.</summary>
    public string Folder => folder;

    /// <summary>When it last wrote, or null before it has.</summary>
    public DateTimeOffset? LastWritten => _last == DateTimeOffset.MinValue ? null : _last;

    /// <summary>Writes if the interval has passed, and says whether it did.</summary>
    public bool Tick(IEnumerable<ResumeData> torrents)
    {
        DateTimeOffset now = time.GetUtcNow();

        if (now - _last < interval)
        {
            return false;
        }

        Write(torrents);

        return true;
    }

    /// <summary>
    /// Writes whatever the interval says, because this is the last chance.
    /// </summary>
    /// <remarks>
    /// A clean stop is the one moment the data is certainly right, and skipping
    /// it because the interval had not elapsed would throw away everything
    /// verified since the last write for no reason at all.
    /// </remarks>
    public void Stop(IEnumerable<ResumeData> torrents)
    {
        Write(torrents);
    }

    /// <summary>Reads one back, or null when there is nothing to read.</summary>
    public ResumeData? Load(string infoHash)
    {
        string path = Path.Combine(folder, ResumeData.FileName(infoHash));

        // A torrent that has never been written, a folder that does not exist
        // yet, a file somebody deleted: all the same answer, which is to verify
        // the torrent.
        return File.Exists(path) ? ResumeData.Read(File.ReadAllBytes(path)) : null;
    }

    /// <summary>
    /// Keeps a torrent's own metadata beside its resume file.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every torrent here is added from a magnet, which carries a hash and no
    /// file list. Without this the client asks the swarm for the metadata again
    /// after every restart — and a swarm that has gone quiet cannot answer, so
    /// the torrent times out and is given up on however complete it is on disk.
    /// </para>
    /// <para>
    /// That is not a corner. On 23 August 2026 the owner restarted the server
    /// with twenty-three finished downloads on disk and thirty-three grabs were
    /// failed for want of metadata for files that were already there. A client
    /// that has the metadata should never have to ask for it again.
    /// </para>
    /// </remarks>
    public void Remember(string infoHash, ReadOnlySpan<byte> info)
    {
        Directory.CreateDirectory(folder);

        File.WriteAllBytes(MetadataPath(infoHash), info);
    }

    /// <summary>A torrent's metadata as it was last seen, or null.</summary>
    public byte[]? Recall(string infoHash)
    {
        string path = MetadataPath(infoHash);

        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    /// <summary>Forgets one, for a torrent that has been removed.</summary>
    public void Forget(string infoHash)
    {
        foreach (string path in new[] { Path.Combine(folder, ResumeData.FileName(infoHash)), MetadataPath(infoHash) })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>Where a torrent's metadata is kept.</summary>
    /// <remarks>
    /// The info dictionary alone, not a whole <c>.torrent</c>: it is what the
    /// info hash is taken over, so a file that has been tampered with fails to
    /// hash and is refused rather than trusted.
    /// </remarks>
    private string MetadataPath(string infoHash)
    {
        return Path.Combine(folder, $"{infoHash.ToUpperInvariant()}.info");
    }

    private void Write(IEnumerable<ResumeData> torrents)
    {
        Directory.CreateDirectory(folder);

        foreach (ResumeData torrent in torrents)
        {
            string path = Path.Combine(folder, ResumeData.FileName(torrent.InfoHash));
            string writing = path + ".writing";

            File.WriteAllBytes(writing, torrent.Write());

            // Moved into place, never written in place: the move is what makes
            // the old file good until the new one is whole.
            File.Move(writing, path, overwrite: true);
        }

        _last = time.GetUtcNow();
    }
}
