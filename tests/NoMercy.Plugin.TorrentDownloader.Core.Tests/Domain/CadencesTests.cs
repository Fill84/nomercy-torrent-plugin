using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Domain;

public class CadencesTests
{
    [Fact]
    public void TheFourCadencesCarryTheirDocumentedDefaults()
    {
        Cadences cadences = new();

        Assert.Equal("* * * * *", cadences.Transfers);
        Assert.Equal("*/15 * * * *", cadences.Feed);
        Assert.Equal("0 */6 * * *", cadences.Search);
        Assert.Equal("0 4 * * *", cadences.Maintenance);
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
}
