using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

/// <summary>
/// When a cadence next runs, worked out the way the media server works it out.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The server's answer, not a second one.</strong> NoMercyQueue's
/// <c>CronService</c> hands the expression to NCrontab with
/// <c>DateTime.UtcNow</c> as the base, so a plugin's cadence fires in UTC and
/// the next time is strictly after the last. Every expected value here is what
/// NCrontab itself answered for the same expression and moment.
/// </para>
/// <para>
/// The owner asked for the dashboard to say when the next run is, as a clock
/// time. The server keeps that time and does not offer it to a plugin, so it is
/// worked out here — and a time that disagreed with when the server really
/// runs it would be worse than none.
/// </para>
/// </remarks>
public class CronNextTests
{
    [Fact]
    public void EverySixHoursFromTwentyPastThreeIsSix()
    {
        Assert.Equal(At(2026, 9, 11, 6, 0), Cron.NextAfter("0 */6 * * *", At(2026, 9, 11, 3, 22)));
    }

    /// <remarks>
    /// Strictly after. A cadence asked at the very minute it fires has just
    /// fired, and the next one is a day away.
    /// </remarks>
    [Fact]
    public void TheMomentItFiresIsNotItsNextTime()
    {
        Assert.Equal(At(2026, 9, 12, 4, 0), Cron.NextAfter("0 4 * * *", At(2026, 9, 11, 4, 0)));
    }

    [Fact]
    public void EveryQuarterOfAnHourCountsFromTheHour()
    {
        Assert.Equal(
            At(2026, 9, 11, 12, 15),
            Cron.NextAfter("*/15 * * * *", At(2026, 9, 11, 12, 0).AddSeconds(30)));
    }

    [Fact]
    public void RangesListsAndStepsTogether()
    {
        // Mondays and Wednesdays, at half past eight and half past ten.
        // 11 September 2026 is a Friday.
        Assert.Equal(At(2026, 9, 14, 8, 30), Cron.NextAfter("30 8-10/2 * * 1,3", At(2026, 9, 11, 12, 0)));
    }

    /// <remarks>
    /// A day of the month and a day of the week together: NCrontab wants both,
    /// so this is a Friday that is also the thirteenth.
    /// </remarks>
    [Fact]
    public void ADayOfTheMonthAndADayOfTheWeekMustBothMatch()
    {
        Assert.Equal(At(2026, 11, 13, 0, 0), Cron.NextAfter("0 0 13 * 5", At(2026, 9, 11, 0, 0)));
    }

    /// <remarks>
    /// An expression the server cannot parse is never scheduled, so it has no
    /// next time — and saying one would be inventing it.
    /// </remarks>
    [Fact]
    public void AnExpressionThatIsNotValidHasNoNextTime()
    {
        Assert.Null(Cron.NextAfter("every six hours", At(2026, 9, 11, 3, 22)));
    }

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute)
    {
        return new(year, month, day, hour, minute, 0, TimeSpan.Zero);
    }
}
