using NoMercy.Plugin.TorrentDownloader.Solver;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// A browser nobody is using is closed, and one still in use is not.
/// </summary>
/// <remarks>
/// <para>
/// The browser is kept between solves on purpose: a gated source hands its
/// clearance to a browser session, and taking that session down loses it, so
/// the next search pays for a fresh challenge. Keeping it was the fix for a
/// gated indexer that answered nothing.
/// </para>
/// <para>
/// Kept for the life of the server, though, that is ten Chrome processes and
/// two hundred megabytes held by a machine that will not search again until
/// morning — which the owner saw on 31 August 2026 and asked about, having been
/// told the day before that it was deliberate.
/// </para>
/// </remarks>
public class TheBrowserIsClosedWhenItHasNothingToDoTests
{
    private static readonly DateTimeOffset Nine = new(2026, 8, 31, 21, 0, 0, TimeSpan.Zero);

    /// <remarks>
    /// A solve in flight keeps it, however long that solve takes. A challenge
    /// can sit for minutes waiting on a turnstile, and closing the browser
    /// under it fails the search that started it.
    /// </remarks>
    [Fact]
    public void ATabStillOpenKeepsTheBrowserHoweverLongItHasBeenOpen()
    {
        Assert.False(IdleBrowser.Due(1, Nine, Nine.AddHours(3), IdleBrowser.After));
    }

    /// <remarks>
    /// And so does a browser that has been started and not yet asked for a tab.
    /// Something started it, which is reason enough: closing it from underneath
    /// is how a solve fails before it begins.
    /// </remarks>
    [Fact]
    public void ABrowserThatHasNotBeenAskedForATabYetIsKept()
    {
        Assert.False(IdleBrowser.Due(0, null, Nine.AddHours(3), IdleBrowser.After));
    }

    /// <remarks>
    /// Within the window it is kept, which is what makes it worth keeping at
    /// all: every source of one search cycle shares the browser the first of
    /// them started, and with it the clearance that browser was given.
    /// </remarks>
    [Fact]
    public void ABrowserIdleForLessThanTheWindowIsKept()
    {
        Assert.False(IdleBrowser.Due(0, Nine, Nine.Add(IdleBrowser.After).AddSeconds(-1), IdleBrowser.After));
    }

    /// <remarks>
    /// Past it, it goes. The next gated search pays for one challenge, which is
    /// what it cost before this browser was ever kept, and an evening with
    /// nothing to look for gets its memory back.
    /// </remarks>
    [Fact]
    public void ABrowserIdleForLongerThanTheWindowIsClosed()
    {
        Assert.True(IdleBrowser.Due(0, Nine, Nine.Add(IdleBrowser.After), IdleBrowser.After));
        Assert.True(IdleBrowser.Due(0, Nine, Nine.AddHours(3), IdleBrowser.After));
    }
}
