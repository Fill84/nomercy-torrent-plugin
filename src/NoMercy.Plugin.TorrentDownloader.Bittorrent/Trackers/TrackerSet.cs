namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Sending bytes to a tracker and getting bytes back.
/// </summary>
/// <remarks>
/// The seam the wire sits behind. Everything above it — when to announce, which
/// event, how long a connection id lives, what to do when one tracker will not
/// answer — is decided here and tested without a socket; what is below is a
/// GET and a datagram and has nothing to decide.
/// </remarks>
public interface ITrackerTransport
{
    /// <summary>Fetches an address and answers the bytes.</summary>
    Task<byte[]> GetAsync(Uri address, CancellationToken ct);

    /// <summary>
    /// Sends one datagram and waits for one back.
    /// </summary>
    /// <exception cref="TimeoutException">Nothing came back in time.</exception>
    Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct);
}

/// <summary>What one tracker answered, or why it did not.</summary>
/// <param name="Tracker">Which one.</param>
/// <param name="Response">What it said, or null when it said nothing usable.</param>
/// <param name="Failure">Why not, in words.</param>
public sealed record TrackerResult(string Tracker, AnnounceResponse? Response, string? Failure = null);

/// <summary>
/// Every tracker a torrent knows, announced to together.
/// </summary>
/// <remarks>
/// <para>
/// All of them at once, and one failing does not stop the others: a torrent
/// with six trackers where the first is down is a torrent that still has five,
/// and 0.3.4's habit of stopping at the first refusal is how a swarm with
/// hundreds of peers looked empty.
/// </para>
/// <para>
/// Connection ids are kept per tracker for the minute BEP 15 allows. Asking for
/// one before every announce doubles every announce; using one past its minute
/// earns an error instead of peers.
/// </para>
/// </remarks>
public sealed class TrackerSet(ITrackerTransport transport, TimeProvider time)
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Connection> _connections = new(StringComparer.OrdinalIgnoreCase);
    private int _transaction = Random.Shared.Next();

    /// <summary>How long a tracker is given to answer one datagram.</summary>
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Announces to every tracker at once.
    /// </summary>
    /// <remarks>
    /// One result per tracker, in the order they were given, whether it
    /// answered or not — a page that lists trackers has to be able to say which
    /// of them is the one refusing.
    /// </remarks>
    public async Task<IReadOnlyList<TrackerResult>> AnnounceAsync(
        IReadOnlyList<string> trackers,
        AnnounceRequest request,
        CancellationToken ct)
    {
        return await Task.WhenAll(trackers.Select(tracker => AnnounceOneAsync(tracker, request, ct)));
    }

    /// <summary>How long one tracker is given, every retry of it together.</summary>
    /// <remarks>
    /// <para>
    /// BEP 15 says to wait <c>15 * 2^n</c> seconds before retransmitting, over
    /// eight tries. That is thirty-two minutes spent on a tracker that is not
    /// there — and an announce is one <see cref="Task.WhenAll(Task[])"/>, so
    /// every tracker that <em>was</em> going to answer waited those thirty-two
    /// minutes with it.
    /// </para>
    /// <para>
    /// A magnet off a public indexer carries eighteen trackers and several of
    /// them are years dead. Measured on 31 August 2026 against the owner's Dark
    /// Matter pack: no announce came back at all for the first two and a half
    /// minutes, and the one that ended it was a deadline above this throwing —
    /// which the catch below did not take, so the whole announce came back
    /// empty and the torrent sat at no peers with no swarm count while four
    /// trackers had three hundred seeds to hand over.
    /// </para>
    /// <para>
    /// Forty-five seconds is one connect, one announce and one retry of each at
    /// <see cref="Patience"/>. A tracker slower than that is one the swarm can
    /// be found without.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(45);

    /// <summary>Announces to one, whichever protocol it speaks.</summary>
    public async Task<TrackerResult> AnnounceOneAsync(
        string tracker,
        AnnounceRequest request,
        CancellationToken ct)
    {
        // Its own clock, so one tracker cannot hold the announce open for the
        // rest of them, and the injected TimeProvider so this is testable
        // without waiting forty-five real seconds.
        using CancellationTokenSource own = new(Deadline, time);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct, own.Token);

        try
        {
            AnnounceResponse response = tracker.StartsWith("udp:", StringComparison.OrdinalIgnoreCase)
                ? await UdpAsync(tracker, request, waiting.Token)
                : HttpAnnounce.Read(await transport.GetAsync(HttpAnnounce.Address(tracker, request), waiting.Token));

            return new(tracker, response, response.Failure);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // This tracker's deadline, not the caller's. It used to go straight
            // past the catch below and out of AnnounceAsync, which took every
            // other tracker's answer with it.
            return new(tracker, null, $"It did not answer within {Deadline.TotalSeconds:0} seconds.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One tracker is one tracker. The others are still worth asking,
            // and the reason this one did not answer belongs on the page rather
            // than in a stack trace nobody sees.
            return new(tracker, null, exception.Message);
        }
    }

    /// <summary>
    /// Connect if the id has expired, then announce, retrying as BEP 15 says.
    /// </summary>
    private async Task<AnnounceResponse> UdpAsync(string tracker, AnnounceRequest request, CancellationToken ct)
    {
        Uri address = new(tracker);
        string host = address.Host;
        int port = address.Port;

        for (int attempt = 0; attempt < UdpAnnounce.Tries; attempt++)
        {
            try
            {
                long connection = await ConnectionAsync(tracker, host, port, ct);
                int transaction = NextTransaction();

                byte[] answer = await transport.ExchangeAsync(
                    host,
                    port,
                    UdpAnnounce.AnnounceRequest(connection, transaction, request),
                    Patience,
                    ct);

                return UdpAnnounce.ReadAnnounce(answer, transaction);
            }
            catch (TimeoutException) when (attempt + 1 < UdpAnnounce.Tries)
            {
                // Lost, which UDP does silently. Wait as long as the spec says
                // and ask again; a client that retried tightly is one the
                // tracker blocks.
                Forget(tracker);

                await Task.Delay(UdpAnnounce.Backoff(attempt), time, ct);
            }
        }

        throw new TrackerException($"{tracker} did not answer after {UdpAnnounce.Tries} tries.");
    }

    /// <summary>The connection id for this tracker, asking for one only when it has expired.</summary>
    private async Task<long> ConnectionAsync(string tracker, string host, int port, CancellationToken ct)
    {
        DateTimeOffset now = time.GetUtcNow();

        lock (_lock)
        {
            if (_connections.TryGetValue(tracker, out Connection held) && held.Until > now)
            {
                return held.Id;
            }
        }

        int transaction = NextTransaction();

        byte[] answer = await transport.ExchangeAsync(
            host,
            port,
            UdpAnnounce.ConnectRequest(transaction),
            Patience,
            ct);

        long id = UdpAnnounce.ReadConnect(answer, transaction);

        lock (_lock)
        {
            _connections[tracker] = new(id, now + UdpAnnounce.ConnectionIdLife);
        }

        return id;
    }

    private void Forget(string tracker)
    {
        lock (_lock)
        {
            _connections.Remove(tracker);
        }
    }

    /// <summary>
    /// A number the tracker echoes back.
    /// </summary>
    /// <remarks>
    /// Different every time and never reused: UDP has no connection, and an
    /// answer to somebody else's question arrives at this socket looking
    /// exactly like an answer to ours.
    /// </remarks>
    private int NextTransaction()
    {
        return Interlocked.Increment(ref _transaction);
    }

    private readonly record struct Connection(long Id, DateTimeOffset Until);
}
