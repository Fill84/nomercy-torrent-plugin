namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// A token bucket: bytes a second, and what is left of this second.
/// </summary>
/// <remarks>
/// <para>
/// It starts empty. A bucket that started full would let a torrent send a whole
/// second's worth the instant the plugin came up — which is exactly when the
/// owner is most likely to be watching something and least likely to forgive
/// the stutter.
/// </para>
/// <para>
/// A second's worth is also as much as it will ever hold. Without that cap, a
/// client that was idle for an hour would arrive with an hour of allowance and
/// saturate the line while it spent it.
/// </para>
/// </remarks>
public sealed class TokenBucket(long bytesPerSecond, TimeProvider time)
{
    private double _available;
    private DateTimeOffset _last = time.GetUtcNow();

    /// <summary>
    /// Bytes a second, or nought for no limit.
    /// </summary>
    /// <remarks>
    /// Settable while running, because the owner changing a limit on the
    /// Settings page must not need a restart — the point of a limit is usually
    /// that something is happening now.
    /// </remarks>
    public long BytesPerSecond { get; set; } = bytesPerSecond;

    /// <summary>Whether this bucket lets everything through.</summary>
    public bool Unlimited => BytesPerSecond <= 0;

    /// <summary>How much may go now, without taking it.</summary>
    public long Available(long wanted)
    {
        Refill();

        return Unlimited ? wanted : (long)Math.Min(wanted, _available);
    }

    /// <summary>Takes what has already been agreed.</summary>
    /// <remarks>
    /// Separate from <see cref="Available"/> because two buckets have to agree
    /// before either is drained: the lower of the two decides, and the other
    /// must not be charged for bytes that never went anywhere.
    /// </remarks>
    public void Consume(long bytes)
    {
        if (!Unlimited)
        {
            _available -= bytes;
        }
    }

    /// <summary>What may go now, taken.</summary>
    public long Take(long wanted)
    {
        long allowed = Available(wanted);

        Consume(allowed);

        return allowed;
    }

    private void Refill()
    {
        DateTimeOffset now = time.GetUtcNow();
        double seconds = (now - _last).TotalSeconds;

        _last = now;

        if (Unlimited)
        {
            return;
        }

        // Capped at one second's worth, however long it has been.
        _available = Math.Min(BytesPerSecond, _available + (seconds * BytesPerSecond));
    }
}

/// <summary>
/// Every limit at once: one pair for the whole client, one pair per torrent.
/// </summary>
/// <remarks>
/// The lower of the two wins, which is the only sane reading: a per-torrent
/// limit above the global one would let three torrents each take the whole
/// line, and a global limit above a per-torrent one is not a licence to ignore
/// what the owner said about that torrent.
/// </remarks>
public sealed class RateLimits(TimeProvider time)
{
    private readonly Dictionary<string, TokenBucket> _download = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TokenBucket> _upload = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The whole client's download limit, in bytes a second. Nought is unlimited.</summary>
    public TokenBucket Download { get; } = new(0, time);

    /// <summary>The whole client's upload limit.</summary>
    public TokenBucket Upload { get; } = new(0, time);

    /// <summary>Sets what one torrent may have, creating its buckets if it has none.</summary>
    public void ForTorrent(string infoHash, long download, long upload)
    {
        Bucket(_download, infoHash).BytesPerSecond = download;
        Bucket(_upload, infoHash).BytesPerSecond = upload;
    }

    /// <summary>How much this torrent may read now.</summary>
    public long TakeDownload(string infoHash, long wanted)
    {
        return Take(Download, Bucket(_download, infoHash), wanted);
    }

    /// <summary>How much this torrent may send now.</summary>
    public long TakeUpload(string infoHash, long wanted)
    {
        return Take(Upload, Bucket(_upload, infoHash), wanted);
    }

    /// <summary>The lower of the two, charged to both.</summary>
    private static long Take(TokenBucket global, TokenBucket mine, long wanted)
    {
        long allowed = Math.Min(global.Available(wanted), mine.Available(wanted));

        global.Consume(allowed);
        mine.Consume(allowed);

        return allowed;
    }

    private TokenBucket Bucket(Dictionary<string, TokenBucket> buckets, string infoHash)
    {
        if (!buckets.TryGetValue(infoHash, out TokenBucket? bucket))
        {
            // Unlimited until somebody says otherwise: a torrent nobody has set
            // a limit for is held by the global one alone.
            bucket = new(0, time);
            buckets[infoHash] = bucket;
        }

        return bucket;
    }
}
