namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// The one thing that slows anything down.
/// </summary>
/// <remarks>
/// <para>
/// Every outbound request goes through here, one gate per hostname. No stage
/// sleeps and no stage knows another source exists: a stage asks for what it
/// wants and the gate decides when it happens.
/// </para>
/// <para>
/// It also owns backoff. A host that says it has had enough gets a wider gap
/// until it stops saying so — but a refusal that is <em>our</em> fault earns
/// none, and that distinction is the whole of B3: 0.3.4 counted the server not
/// having granted a host as the site failing, parked the source for fifteen
/// minutes, and left it parked after the owner approved the host.
/// </para>
/// </remarks>
public sealed class HostGate(TimeProvider time)
{
    /// <summary>How many requests to one host may be in flight.</summary>
    public const int DefaultMaxConcurrent = 2;

    /// <summary>The widest a host's interval can get, however often it refuses.</summary>
    /// <remarks>
    /// A ceiling, because doubling without one turns a site having a bad
    /// afternoon into a source that is effectively gone for the rest of the
    /// week.
    /// </remarks>
    public static readonly TimeSpan MaximumInterval = TimeSpan.FromMinutes(15);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, Host> _hosts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Tells the gate how a host wants to be treated. Without this a host gets
    /// the catalogue's default of one request every fifteen seconds.
    /// </summary>
    public void Configure(string host, TimeSpan minimumInterval, int maxConcurrent = DefaultMaxConcurrent)
    {
        lock (_lock)
        {
            Host gate = Get(host);
            gate.Configured = minimumInterval;
            gate.Interval = minimumInterval;
            gate.InFlight = new(maxConcurrent, maxConcurrent);
        }
    }

    /// <summary>The gap currently being kept to <paramref name="host"/>.</summary>
    public TimeSpan IntervalFor(string host)
    {
        lock (_lock)
        {
            return Get(host).Interval;
        }
    }

    /// <summary>
    /// Runs <paramref name="work"/> when <paramref name="host"/> is ready for it.
    /// </summary>
    public async Task<T> RunAsync<T>(string host, Func<CancellationToken, Task<T>> work, CancellationToken ct)
    {
        Host gate;
        SemaphoreSlim inFlight;

        lock (_lock)
        {
            gate = Get(host);
            inFlight = gate.InFlight;
        }

        await inFlight.WaitAsync(ct);

        try
        {
            while (true)
            {
                TimeSpan wait;

                lock (_lock)
                {
                    DateTimeOffset now = time.GetUtcNow();
                    DateTimeOffset earliest = gate.LastStartedAt + gate.Interval;

                    if (earliest <= now)
                    {
                        // Claimed before the lock is released, so two callers
                        // that both found the host free cannot both go now.
                        gate.LastStartedAt = now;
                        break;
                    }

                    wait = earliest - now;
                }

                // Re-checked after waiting rather than trusted: another caller
                // may have taken the slot while this one slept.
                await Task.Delay(wait, time, ct);
            }

            return await work(ct);
        }
        finally
        {
            inFlight.Release();
        }
    }

    /// <summary>
    /// The host said it has had enough — 429, 503 or 509. Widen the gap.
    /// </summary>
    public void Refused(string host)
    {
        lock (_lock)
        {
            Host gate = Get(host);
            TimeSpan doubled = gate.Interval * 2;
            gate.Interval = doubled > MaximumInterval ? MaximumInterval : doubled;
        }
    }

    /// <summary>
    /// The host answered. Narrow the gap back towards what it asked for.
    /// </summary>
    /// <remarks>
    /// Halving rather than resetting: a host that refused once is likely to
    /// again, and going straight back to the full rate on the first success is
    /// what makes a site refuse in bursts for ever.
    /// </remarks>
    public void Succeeded(string host)
    {
        lock (_lock)
        {
            Host gate = Get(host);
            TimeSpan halved = gate.Interval / 2;
            gate.Interval = halved < gate.Configured ? gate.Configured : halved;
        }
    }

    /// <summary>
    /// The request never reached the host: the server has not granted it.
    /// </summary>
    /// <remarks>
    /// Deliberately does nothing at all. It is not the site failing and it is
    /// not the site refusing — it is this plugin not being allowed to ask, and
    /// punishing a host for that leaves it parked long after the owner has said
    /// yes. It is reported and the host is skipped for the cycle.
    /// </remarks>
    public void NotPermitted(string host)
    {
        _ = host;
    }

    private Host Get(string host)
    {
        if (!_hosts.TryGetValue(host, out Host? gate))
        {
            gate = new();
            _hosts[host] = gate;
        }

        return gate;
    }

    private sealed class Host
    {
        public TimeSpan Configured { get; set; } = TimeSpan.FromSeconds(15);

        public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(15);

        public SemaphoreSlim InFlight { get; set; } = new(DefaultMaxConcurrent, DefaultMaxConcurrent);

        /// <summary>
        /// Long ago, so the first request to a host is not made to wait for a
        /// gap after a request that never happened.
        /// </summary>
        public DateTimeOffset LastStartedAt { get; set; } = DateTimeOffset.MinValue;
    }
}
