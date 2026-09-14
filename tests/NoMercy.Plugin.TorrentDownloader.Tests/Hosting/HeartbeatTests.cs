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
            () => true,
            () => moved.Set(),
            new CapturingLogger(),
            clock);

        beat.StartBeating();

        Assert.True(BeatUntil(clock, started), "the first tick never looked.");

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
            () => true,
            () => moved.Set(),
            log,
            clock);

        beat.StartBeating();

        Assert.True(BeatUntil(clock, started), "the first tick never looked.");

        DateTimeOffset lookedAt = clock.GetUtcNow();

        while (clock.GetUtcNow() < lookedAt + Heartbeat.Patience + Heartbeat.Every)
        {
            clock.Advance(Heartbeat.Every);
        }

        Assert.Single(log.Entries, entry => entry.Level == LogLevel.Warning && entry.Line.Contains("has not answered", StringComparison.Ordinal));

        release.Set();

        Assert.True(moved.Wait(TimeSpan.FromSeconds(10)), "what the look found was never told to the pages.");
        Assert.Single(log.Entries, entry => entry.Line.Contains("answered again", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// <strong>An idle client is not asked anything.</strong> This sampled
    /// <c>BittorrentEngine.Drawn</c> once a second from the moment the client
    /// started — on every server, whether anything was downloading or not, and
    /// whether or not a page was open. Each reading takes the client's lock and
    /// builds a string over every torrent it holds.
    /// </para>
    /// <para>
    /// <c>BittorrentEngine.Watching</c> was written for exactly this — "whether
    /// there is any torrent at all to draw" — and was wired to nothing. It was
    /// dead code while the work it was meant to stop ran on.
    /// </para>
    /// <para>
    /// <strong>Watching, not moving.</strong> The gate is whether a torrent is
    /// held, never whether bytes are arriving: a stalled download is the one
    /// being stared at, and its peers, seeds and chokes are exactly what say
    /// what is happening to it. Nought bytes a second is news. That distinction
    /// is `S11-29` and it must survive this.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingIsSampledWhileThereIsNothingToWatch()
    {
        FakeTimeProvider clock = new();
        using ManualResetEventSlim looked = new(false);
        int readings = 0;
        bool watching = false;

        using Heartbeat beat = new(
            () =>
            {
                Interlocked.Increment(ref readings);
                looked.Set();

                return "one torrent";
            },
            () => watching,
            () => { },
            new CapturingLogger(),
            clock);

        beat.StartBeating();

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, readings);

        // And the moment there is something to watch, it is looked at.
        watching = true;

        clock.Advance(Heartbeat.Every);

        // Waited for: the look runs on a thread of its own, so advancing the
        // clock returns before it has been made.
        Assert.True(looked.Wait(TimeSpan.FromSeconds(10)), "a held torrent was never looked at.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A heartbeat nobody started does not beat.</strong> This was a
    /// timer set in the constructor to go off every second for the life of the
    /// server. Gating what it did on whether a torrent was held stopped the
    /// work, and not the waking: once a second, for ever, on every server, it
    /// woke to find it had nothing to do.
    /// </para>
    /// <para>
    /// It beats only between <see cref="Heartbeat.StartBeating"/> and
    /// <see cref="Heartbeat.StopBeating"/> now, which is while somebody has a page open
    /// — see <c>Onlookers</c>. The test counts wakings rather than readings,
    /// because a waking that reads nothing is the cost this removes.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHeartbeatBeatsOnlyBetweenStartAndStop()
    {
        FakeTimeProvider clock = new();
        int woken = 0;

        using Heartbeat beat = new(
            () => null,
            () =>
            {
                Interlocked.Increment(ref woken);

                return false;
            },
            () => { },
            new CapturingLogger(),
            clock);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(0, woken);

        beat.StartBeating();

        clock.Advance(TimeSpan.FromSeconds(10) * Heartbeat.Every.TotalSeconds);

        Assert.True(woken > 0, "a started heartbeat never woke.");

        beat.StopBeating();

        int atStop = woken;

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal(atStop, woken);

        // And it can be started again, which is a page opened after one closed.
        beat.StartBeating();

        clock.Advance(Heartbeat.Every);

        Assert.True(woken > atStop, "a heartbeat started a second time never woke.");
    }

    /// <remarks>
    /// <para>
    /// <strong>A beat is compared with what the page was drawn with.</strong> A
    /// page that has just been fetched already shows what it shows, and pushing
    /// that to it again is a whole fetch of the view for nothing — the empty push
    /// the owner asked to be rid of. So what the page was drawn with is handed to
    /// the heartbeat, and only a reading that differs from it is news.
    /// </para>
    /// <para>
    /// <strong>Not "the first beat pushes nothing", which was the first shape
    /// and was wrong.</strong> A rate is measured between two readings, and with
    /// nobody looking the client can go unread for hours — so the page fetched
    /// after that draws the average over those hours. The first beat a second
    /// later measures the real rate; taken as a baseline, it matched the next beat
    /// and nothing was ever pushed, and a stalled torrent went on showing a speed
    /// it had not had for an hour. Compared with what the page drew instead, that
    /// first beat differs, and the page is put right within a second.
    /// </para>
    /// </remarks>
    [Fact]
    public void ABeatIsComparedWithWhatThePageWasDrawnWith()
    {
        FakeTimeProvider clock = new();
        using ManualResetEventSlim read = new(false);
        string now = "twelve per cent at nought bytes a second";
        int moved = 0;

        using Heartbeat beat = new(
            () =>
            {
                read.Set();

                return Volatile.Read(ref now);
            },
            () => true,
            () => Interlocked.Increment(ref moved),
            new CapturingLogger(),
            clock);

        // The page was drawn with exactly what the client says now.
        beat.Shown("twelve per cent at nought bytes a second");
        beat.StartBeating();

        for (int tick = 0; tick < 5; tick++)
        {
            read.Reset();
            Assert.True(BeatUntil(clock, read), "a beat never read.");
        }

        Assert.Equal(0, Volatile.Read(ref moved));
    }

    /// <remarks>
    /// The case that was broken: the page drew an average over the hours nobody
    /// looked, and the torrent is standing still. The first beat says so.
    /// </remarks>
    [Fact]
    public void APageDrawnWithAStaleRateIsPutRightByTheFirstBeat()
    {
        FakeTimeProvider clock = new();
        using ManualResetEventSlim told = new(false);

        using Heartbeat beat = new(
            () => "twelve per cent at nought bytes a second",
            () => true,
            () => told.Set(),
            new CapturingLogger(),
            clock);

        // What the page was drawn with: the rate over the hour since the client
        // was last read, which is not what it is doing now.
        beat.Shown("twelve per cent at 280 kilobytes a second");
        beat.StartBeating();

        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (!told.IsSet && DateTimeOffset.UtcNow < giveUpAt)
        {
            clock.Advance(Heartbeat.Every);
            told.Wait(TimeSpan.FromMilliseconds(50));
        }

        Assert.True(told.IsSet, "a page showing a speed the torrent does not have was never put right.");
    }

    /// <summary>Beats until a reading has been taken, or gives up.</summary>
    /// <remarks>
    /// A beat reads on a thread of its own, and a beat that arrives while the
    /// last reading is still being taken is dropped — so one advance of the clock
    /// is not always one reading, and a test that assumed so would be measuring
    /// the thread pool.
    /// </remarks>
    private static bool BeatUntil(FakeTimeProvider clock, ManualResetEventSlim read)
    {
        DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(10);

        while (!read.IsSet && DateTimeOffset.UtcNow < giveUpAt)
        {
            clock.Advance(Heartbeat.Every);

            read.Wait(TimeSpan.FromMilliseconds(50));
        }

        return read.IsSet;
    }
}
