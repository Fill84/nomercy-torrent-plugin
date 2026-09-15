using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

public class CadencesTests
{
    /// <remarks>
    /// <strong>One, where there were four.</strong> Transfers every minute,
    /// feed every fifteen, search every six hours and maintenance at four in
    /// the morning were never four schedules: they are the steps of one cycle,
    /// each started by the last one finishing. What is left to ask is how often
    /// to start one when nobody has, and the owner said hourly.
    /// </remarks>
    [Fact]
    public void TheOneCadenceCarriesItsDocumentedDefault()
    {
        Cadences cadences = new();

        Assert.Equal("0 * * * *", cadences.Cycle);
        Assert.Equal(("cycle", "0 * * * *"), Assert.Single(cadences.All()));
    }

    /// <remarks>
    /// Five fields, and every one of them checked. A cron the server cannot
    /// parse is not rejected at registration: the job is simply never
    /// scheduled, and the owner is left with a plugin that looks configured and
    /// never runs.
    /// </remarks>
    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/15 * * * *")]
    [InlineData("0 */6 * * *")]
    [InlineData("0 4 * * *")]
    [InlineData("30 2 1 1 0")]
    [InlineData("0,30 8-17 * * 1-5")]
    [InlineData("59 23 31 12 6")]
    public void ARealCronIsAccepted(string expression)
    {
        Assert.True(Cron.IsValid(expression, out string? reason), reason);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("", "five fields")]
    [InlineData("   ", "five fields")]
    [InlineData("* * * *", "five fields")]
    [InlineData("* * * * * *", "five fields")]
    [InlineData("60 * * * *", "minute")]
    [InlineData("* 24 * * *", "hour")]
    [InlineData("* * 0 * *", "day of the month")]
    [InlineData("* * 32 * *", "day of the month")]
    [InlineData("* * * 13 *", "month")]
    [InlineData("* * * 0 *", "month")]
    [InlineData("* * * * 7", "day of the week")]
    [InlineData("*/0 * * * *", "step")]
    [InlineData("every minute", "minute")]
    [InlineData("5-2 * * * *", "minute")]
    public void ACronThatIsNotOneIsRefusedWithTheReason(string expression, string expectedInReason)
    {
        Assert.False(Cron.IsValid(expression, out string? reason));
        Assert.NotNull(reason);
        Assert.Contains(expectedInReason, reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The reason has to name the field, or the owner is told "invalid" about
    /// an expression with five fields in it and has to guess which one.
    /// </remarks>
    [Fact]
    public void TheReasonNamesTheFieldAndTheValue()
    {
        Cron.IsValid("* 24 * * *", out string? reason);

        Assert.NotNull(reason);
        Assert.Contains("hour", reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("24", reason, StringComparison.Ordinal);
    }

    /// <remarks>
    /// <para>
    /// <strong>Never sooner than fifteen minutes apart.</strong> The owner's requirement of
    /// 15 September 2026 (<c>docs/specs/release-names.md</c>): the shortest interval accepted is 15
    /// minutes and any longer one is accepted. It replaces the hourly floor of 13 September.
    /// </para>
    /// <para>
    /// <strong>Worked out from the times it fires, not from how it is written.</strong> Every minute of
    /// the day the minute and hour fields allow is listed, and no two neighbours — the last of the day
    /// and the first of the next included — may be closer than fifteen minutes. So <c>*/15</c> and
    /// <c>0,20,40</c> pass, while <c>*/25</c> fails: it fires at 50 and again at 0, ten minutes later.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("*/15 * * * *")]
    [InlineData("*/30 * * * *")]
    [InlineData("0,20,40 * * * *")]
    [InlineData("0 * * * *")]
    [InlineData("15 */6 * * *")]
    [InlineData("0 4 * * *")]
    [InlineData("59 23 * * 0")]
    public void ACycleAtLeastFifteenMinutesApartIsAccepted(string expression)
    {
        Assert.True(Cron.AtLeastFifteenMinutesApart(expression, out string? reason), reason);
        Assert.Null(reason);
    }

    [Theory]
    [InlineData("* * * * *")]
    [InlineData("*/5 * * * *")]
    [InlineData("*/25 * * * *")]
    [InlineData("0,10 * * * *")]
    [InlineData("55,5 * * * *")]
    [InlineData("58,3 0,23 * * *")]
    public void ACycleSoonerThanFifteenMinutesApartIsRefusedWithTheReason(string expression)
    {
        Assert.False(Cron.AtLeastFifteenMinutesApart(expression, out string? reason));
        Assert.NotNull(reason);
        Assert.Contains("15 minutes", reason, StringComparison.Ordinal);
    }
}
