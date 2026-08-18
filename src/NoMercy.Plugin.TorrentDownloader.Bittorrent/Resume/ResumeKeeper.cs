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

    /// <summary>Forgets one, for a torrent that has been removed.</summary>
    public void Forget(string infoHash)
    {
        string path = Path.Combine(folder, ResumeData.FileName(infoHash));

        if (File.Exists(path))
        {
            File.Delete(path);
        }
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
