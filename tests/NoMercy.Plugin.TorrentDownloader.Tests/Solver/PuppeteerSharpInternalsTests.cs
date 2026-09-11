using NoMercy.Plugin.TorrentDownloader.Solver;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// What the plugin reaches for inside PuppeteerSharp is still there.
/// </summary>
/// <remarks>
/// <see cref="UnobservedContexts"/> reads a private task PuppeteerSharp fails
/// on every navigation, because left alone it lands in the media server's log
/// as an <c>UnobservedTaskException</c> — eight of them per warm-up on the
/// owner's server on 11 September 2026. It reaches for a field, two properties
/// and an event that are not PuppeteerSharp's public surface. If an update
/// renames any of them, the code quietly does nothing and the lines come back;
/// this is what says so first.
/// </remarks>
public class PuppeteerSharpInternalsTests
{
    [Fact]
    public void EverythingTheContextWatchReachesForIsWhereItWas()
    {
        Assert.True(
            UnobservedContexts.Reachable,
            "PuppeteerSharp no longer has Frame.MainWorld, Frame.PuppeteerWorld, "
            + "IsolatedWorld._contextResolveTaskWrapper and IsolatedWorld.ContextCleared as 25.6.0 did. "
            + "Look at IsolatedWorld.ClearContext in the new version before changing anything.");
    }
}
