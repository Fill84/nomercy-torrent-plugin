using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// How fast a transfer is moving now.
/// </summary>
/// <remarks>
/// Now, not on average since it started. A torrent that downloaded a gigabyte
/// in its first minute and has been stalled for an hour since is not moving at
/// seventeen megabytes a second, and a page that said so would have the owner
/// waiting on a download that had stopped.
/// </remarks>
public class RateMeterTests
{
    [Fact]
    public void TheRateIsWhatMovedSinceTheLastReadingAndNotTheAverage()
    {
        FakeTimeProvider clock = new();
        RateMeter meter = new(clock);

        // The first reading has nothing to measure against, so it is nought
        // rather than a number invented out of one sample.
        Assert.Equal(0, meter.Measure(0));

        clock.Advance(TimeSpan.FromSeconds(1));

        Assert.Equal(1_000_000, meter.Measure(1_000_000));

        // Stalled for a minute. The average over the whole transfer is still
        // sixteen kilobytes a second; what is happening now is nothing.
        clock.Advance(TimeSpan.FromSeconds(60));

        Assert.Equal(0, meter.Measure(1_000_000));
    }

    /// <remarks>
    /// Two readings in the same instant would divide by nought, and a page
    /// drawing the answer would print something that is not a number. It
    /// answers what it last measured instead, which is the truest thing it has.
    /// </remarks>
    [Fact]
    public void TwoReadingsInOneInstantDoNotMakeANumberThatIsNotOne()
    {
        FakeTimeProvider clock = new();
        RateMeter meter = new(clock);

        meter.Measure(0);
        clock.Advance(TimeSpan.FromSeconds(2));

        double measured = meter.Measure(2_000_000);

        Assert.Equal(1_000_000, measured);

        // No time at all, and more bytes: a page rendering twice in one tick.
        Assert.Equal(measured, meter.Measure(3_000_000));
    }

    /// <remarks>
    /// A reading a few milliseconds after the last is noise, not a measurement:
    /// one sixteen-kibibyte block arriving in four milliseconds is four
    /// megabytes a second, and a dashboard redrawing on every push would show
    /// that and then nought and then that again.
    /// </remarks>
    [Fact]
    public void AReadingTakenTooSoonAfterTheLastKeepsTheOneBeforeIt()
    {
        FakeTimeProvider clock = new();
        RateMeter meter = new(clock);

        meter.Measure(0);
        clock.Advance(TimeSpan.FromSeconds(4));

        Assert.Equal(250_000, meter.Measure(1_000_000));

        clock.Advance(TimeSpan.FromMilliseconds(4));

        Assert.Equal(250_000, meter.Measure(1_016_384));
    }
}
