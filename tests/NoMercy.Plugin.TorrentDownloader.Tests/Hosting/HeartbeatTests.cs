using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The once-a-second look at the client that tells the pages when to move.
/// </summary>
public class HeartbeatTests
{
    /// <remarks>
    /// <para>
    /// <strong>A tick that finds the last one still reading is dropped.</strong>
    /// The timer fired every second whether or not the last look had come
    /// back, and a look that could not get the client's lock sat on a thread
    /// until it could. On 7 September 2026 the client stopped answering at
    /// 00:03 UTC and the server made a new thread every second for ninety
    /// minutes — one per tick, each stuck behind the same lock — until the
    /// pool had nothing left to answer a page with and the owner restarted
    /// it. A plugin that has stopped must look stopped, not take the server
    /// down with it.
    /// </para>
    /// </remarks>
    [Fact]
    public void ATickThatFindsTheLastOneStillReadingIsDropped()
    {
        FakeTimeProvider clock = new();
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim moved = new(false);
        int readings = 0;

        using Heartbeat beat = new(
            () =>
            {
                Interlocked.Increment(ref readings);
                started.Set();
                release.Wait(TimeSpan.FromSeconds(30));

                return "one torrent";
            },
            () => moved.Set(),
            new CapturingLogger(),
            clock);

        clock.Advance(Heartbeat.Every);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the first tick never looked.");

        for (int tick = 0; tick < 5; tick++)
        {
            clock.Advance(Heartbeat.Every);
        }

        Assert.Equal(1, Volatile.Read(ref readings));

        release.Set();

        Assert.True(moved.Wait(TimeSpan.FromSeconds(10)), "what the look found was never told to the pages.");
    }

    /// <remarks>
    /// Said once, in words that name the wait, so the next time the client
    /// stops answering the log says when it started rather than nothing at
    /// all — and said again when it comes back, so the two lines bracket the
    /// hang. The 7 September log had no line of its own for ninety minutes;
    /// the only trace was a thread number climbing.
    /// </remarks>
    [Fact]
    public void AClientThatStopsAnsweringIsSaidOnceAndItsReturnOnce()
    {
        FakeTimeProvider clock = new();
        using ManualResetEventSlim started = new(false);
        using ManualResetEventSlim release = new(false);
        using ManualResetEventSlim moved = new(false);
        CapturingLogger log = new();

        using Heartbeat beat = new(
            () =>
            {
                started.Set();
                release.Wait(TimeSpan.FromSeconds(30));

                return "one torrent";
            },
            () => moved.Set(),
            log,
            clock);

        clock.Advance(Heartbeat.Every);

        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the first tick never looked.");

        while (clock.GetUtcNow() < clock.Start + Heartbeat.Patience + Heartbeat.Every)
        {
            clock.Advance(Heartbeat.Every);
        }

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains("has not answered", StringComparison.Ordinal));

        release.Set();

        Assert.True(moved.Wait(TimeSpan.FromSeconds(10)), "what the look found was never told to the pages.");
        Assert.Single(log.Entries, entry => entry.Line.Contains("answered again", StringComparison.Ordinal));
    }
}
