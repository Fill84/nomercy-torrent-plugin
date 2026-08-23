namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// One peer, as the choking round sees it.
/// </summary>
/// <param name="Address">Which peer.</param>
/// <param name="Interested">Whether it wants anything we have.</param>
/// <param name="DownloadRateBytesPerSecond">What it has been sending us.</param>
/// <param name="UploadRateBytesPerSecond">What we have been sending it.</param>
public sealed record ChokePeer(
    string Address,
    bool Interested,
    double DownloadRateBytesPerSecond,
    double UploadRateBytesPerSecond);

/// <summary>
/// Who is allowed to ask us for pieces.
/// </summary>
/// <remarks>
/// <para>
/// Tit for tat: the four interested peers sending us the most get to ask, and
/// everybody else waits. It is what makes a swarm work at all — a client that
/// unchoked everybody would spread its upload so thin that nobody would find it
/// worth reciprocating.
/// </para>
/// <para>
/// And one at random every thirty seconds, whatever its rate. Without that, a
/// peer that has never been given anything can never prove it would send back,
/// and a client would keep the same four for ever — including four that have
/// quietly stopped being the best.
/// </para>
/// </remarks>
public sealed class ChokeCycle(TimeProvider time, Random random)
{
    private DateTimeOffset _lastRound = DateTimeOffset.MinValue;
    private DateTimeOffset _lastOptimistic = DateTimeOffset.MinValue;
    private string? _optimistic;
    private HashSet<string> _unchoked = new(StringComparer.Ordinal);

    /// <summary>How many are unchoked on merit. Four, from docs/06-torrent-client.md.</summary>
    public const int OnMerit = 4;

    /// <summary>How often the four are worked out again.</summary>
    public static TimeSpan Round { get; } = TimeSpan.FromSeconds(10);

    /// <summary>How often the one chosen at random changes.</summary>
    public static TimeSpan Optimistic { get; } = TimeSpan.FromSeconds(30);

    /// <summary>Who is unchoked as things stand.</summary>
    public IReadOnlySet<string> Unchoked => _unchoked;

    /// <summary>The one being given a chance, or null before the first round.</summary>
    public string? Chance => _optimistic;

    /// <summary>
    /// Works out who should be unchoked, if it is time to.
    /// </summary>
    /// <param name="peers">Everybody connected, with their rates.</param>
    /// <param name="seeding">
    /// Whether this torrent is finished. While seeding there is nothing to
    /// download, so the four are ranked by what we manage to send them — a rate
    /// of nought each way would otherwise make the choice arbitrary and leave
    /// the fastest peers choked.
    /// </param>
    public IReadOnlySet<string> Tick(IReadOnlyList<ChokePeer> peers, bool seeding)
    {
        DateTimeOffset now = time.GetUtcNow();

        if (now - _lastRound < Round)
        {
            // Not yet. Choking more often than every ten seconds makes peers
            // spend their time re-requesting rather than transferring, which is
            // why BEP 3 gives a number at all.
            return _unchoked;
        }

        _lastRound = now;

        ChokePeer[] interested = [.. peers.Where(one => one.Interested)];

        HashSet<string> chosen = new(
            interested
                .OrderByDescending(one => seeding ? one.UploadRateBytesPerSecond : one.DownloadRateBytesPerSecond)
                .Take(OnMerit)
                .Select(one => one.Address),
            StringComparer.Ordinal);

        if (now - _lastOptimistic >= Optimistic || _optimistic is null || !chosen.Contains(_optimistic))
        {
            // Somebody who is not already in on merit, so the chance is a
            // chance rather than a fifth slot given to one of the same four.
            string[] waiting = [.. interested.Where(one => !chosen.Contains(one.Address)).Select(one => one.Address)];

            if (waiting.Length > 0 && now - _lastOptimistic >= Optimistic)
            {
                _optimistic = waiting[random.Next(waiting.Length)];
                _lastOptimistic = now;
            }
            else if (_optimistic is not null && !interested.Any(one => one.Address == _optimistic))
            {
                // It has gone, or stopped being interested. Nothing to keep a
                // slot open for.
                _optimistic = null;
            }
        }

        if (_optimistic is not null)
        {
            chosen.Add(_optimistic);
        }

        _unchoked = chosen;

        return _unchoked;
    }
}

/// <summary>
/// When to stop seeding.
/// </summary>
/// <param name="Ratio">Uploaded over downloaded, at which to stop. Nought is never.</param>
/// <param name="For">How long to seed at most. Zero is never.</param>
public sealed record SeedLimit(double Ratio, TimeSpan For)
{
    /// <summary>
    /// Whether this torrent is done seeding.
    /// </summary>
    /// <param name="priv">
    /// Whether the torrent is private. A public one is finished the moment it
    /// is complete: this client never uploads on a public swarm — see
    /// docs/06-torrent-client.md § Uploading — so staying in one gives nothing
    /// to anybody and costs the owner a connection and a slot.
    /// </param>
    /// <param name="ratio">What has been given back so far.</param>
    /// <param name="seeded">How long it has been seeding.</param>
    public bool Reached(bool priv, double ratio, TimeSpan seeded)
    {
        if (!priv)
        {
            return true;
        }

        // Whichever comes first, from docs/06-torrent-client.md. A limit of
        // nought is not a limit of nought seconds: it is nobody asking for one.
        return (Ratio > 0 && ratio >= Ratio) || (For > TimeSpan.Zero && seeded >= For);
    }
}
