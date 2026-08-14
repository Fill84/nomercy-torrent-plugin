using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

public class HostGateTests
{
    /// <remarks>
    /// The gate is the only thing that slows anything down, so if it lets two
    /// requests through together the site does the rate-limiting instead — and
    /// a site that rate-limits us looks exactly like a site with nothing to
    /// offer.
    /// </remarks>
    [Fact]
    public async Task TwoRequestsToOneHostAreNeverCloserThanItsInterval()
    {
        FakeTimeProvider clock = new();
        HostGate gate = new(clock);
        gate.Configure("one.example", TimeSpan.FromSeconds(15));

        List<DateTimeOffset> ran = [];

        await gate.RunAsync("one.example", _ => Record(clock, ran), CancellationToken.None);

        Task second = gate.RunAsync("one.example", _ => Record(clock, ran), CancellationToken.None);

        // Nothing has moved, so the second request has not happened.
        Assert.Single(ran);

        clock.Advance(TimeSpan.FromSeconds(14));
        Assert.Single(ran);

        clock.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, ran.Count);
        Assert.Equal(TimeSpan.FromSeconds(15), ran[1] - ran[0]);
    }

    /// <remarks>
    /// One gate per hostname, not one gate. Ten sources being asked at once is
    /// the ordinary case — harvest fans out over every feed and find over every
    /// indexer — and a shared queue would make the slowest site the speed of
    /// the whole cycle.
    /// </remarks>
    [Fact]
    public async Task TenRequestsToTenHostsRunTogether()
    {
        FakeTimeProvider clock = new();
        HostGate gate = new(clock);

        List<DateTimeOffset> ran = [];

        // Bounded, because the failure this guards against is not a wrong
        // answer but no answer: one gate shared between hosts would leave nine
        // of these waiting on a clock nothing is going to advance, and an
        // unbounded wait would hang the suite instead of failing it.
        await Task.WhenAll(Enumerable.Range(0, 10).Select(index =>
                gate.RunAsync($"host-{index}.example", _ => Record(clock, ran), CancellationToken.None)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        // No clock was advanced, so every one of them went immediately.
        Assert.Equal(10, ran.Count);
    }

    /// <remarks>
    /// Two at a time to one host, which is the default. A third waits for one
    /// of them rather than for the interval.
    /// </remarks>
    [Fact]
    public async Task OnlySoManyRequestsToOneHostAreInFlightAtOnce()
    {
        FakeTimeProvider clock = new();
        HostGate gate = new(clock);
        gate.Configure("one.example", TimeSpan.Zero, maxConcurrent: 2);

        TaskCompletionSource holdOpen = new();
        int started = 0;

        Task[] all =
        [
            .. Enumerable.Range(0, 3).Select(_ => gate.RunAsync(
                "one.example",
                async _ =>
                {
                    Interlocked.Increment(ref started);
                    await holdOpen.Task;
                    return true;
                },
                CancellationToken.None)),
        ];

        Assert.Equal(2, Volatile.Read(ref started));

        holdOpen.SetResult();
        await Task.WhenAll(all);

        Assert.Equal(3, Volatile.Read(ref started));
    }

    /// <remarks>
    /// <strong>B3, first half.</strong> A host that says it has had enough gets
    /// a wider gap, and success narrows it again. Halving rather than resetting:
    /// a host that refused once is likely to again, and going straight back to
    /// full rate on the first success is what makes a site refuse in bursts for
    /// ever.
    /// </remarks>
    [Fact]
    public void ARefusalWidensTheIntervalAndSuccessNarrowsIt()
    {
        HostGate gate = new(new FakeTimeProvider());
        gate.Configure("one.example", TimeSpan.FromSeconds(10));

        gate.Refused("one.example");
        Assert.Equal(TimeSpan.FromSeconds(20), gate.IntervalFor("one.example"));

        gate.Refused("one.example");
        Assert.Equal(TimeSpan.FromSeconds(40), gate.IntervalFor("one.example"));

        gate.Succeeded("one.example");
        Assert.Equal(TimeSpan.FromSeconds(20), gate.IntervalFor("one.example"));
    }

    /// <remarks>
    /// It never narrows past what the site asked for. The configured interval
    /// is the site's own rule, not a starting guess.
    /// </remarks>
    [Fact]
    public void SuccessNeverNarrowsPastWhatTheSiteAskedFor()
    {
        HostGate gate = new(new FakeTimeProvider());
        gate.Configure("one.example", TimeSpan.FromSeconds(10));

        gate.Succeeded("one.example");
        gate.Succeeded("one.example");

        Assert.Equal(TimeSpan.FromSeconds(10), gate.IntervalFor("one.example"));
    }

    /// <remarks>
    /// And it never widens without limit. Doubling with no ceiling turns a site
    /// having a bad afternoon into one that is gone for the rest of the week.
    /// </remarks>
    [Fact]
    public void TheIntervalHasACeiling()
    {
        HostGate gate = new(new FakeTimeProvider());
        gate.Configure("one.example", TimeSpan.FromSeconds(15));

        for (int refusal = 0; refusal < 20; refusal++)
        {
            gate.Refused("one.example");
        }

        Assert.Equal(HostGate.MaximumInterval, gate.IntervalFor("one.example"));
    }

    /// <remarks>
    /// <strong>B3, second half, and the point of it.</strong> A refusal because
    /// the server has not granted the host is not the site failing. 0.3.4
    /// counted it as one, parked the source for fifteen minutes, and left it
    /// parked after the owner approved the host — while the message said
    /// "parked after repeated failures".
    /// </remarks>
    [Fact]
    public void APermissionRefusalEarnsNoBackoffAtAll()
    {
        HostGate gate = new(new FakeTimeProvider());
        gate.Configure("one.example", TimeSpan.FromSeconds(15));

        gate.NotPermitted("one.example");
        gate.NotPermitted("one.example");
        gate.NotPermitted("one.example");

        Assert.Equal(TimeSpan.FromSeconds(15), gate.IntervalFor("one.example"));
    }

    private static Task<bool> Record(TimeProvider clock, List<DateTimeOffset> ran)
    {
        lock (ran)
        {
            ran.Add(clock.GetUtcNow());
        }

        return Task.FromResult(true);
    }
}
