using NoMercy.Plugin.TorrentDownloader.Hosting;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// One cycle at a time.
/// </summary>
/// <remarks>
/// <strong>F3.</strong> 0.3.4 had no overlap protection at all: a thirty-minute
/// cycle against a five-minute cron is six searches running at once, each
/// asking every site the same questions and each able to grab the release the
/// other five just took. What one cycle has decided is state the next cannot
/// see.
/// </remarks>
public class OneAtATimeTests
{
    [Fact]
    public void TheSecondOneIsTurnedAwayWhileTheFirstIsStillGoing()
    {
        OneAtATime running = new();

        Assert.True(running.TryEnter());
        Assert.False(running.TryEnter());
        Assert.True(running.Busy);
    }

    /// <remarks>
    /// And let in once the first has finished. A guard that never opened again
    /// would be a plugin that ran one cycle and then nothing for as long as the
    /// server was up — the same silence as no cycles at all, and harder to see.
    /// </remarks>
    [Fact]
    public void TheNextOneIsLetInOnceTheFirstHasFinished()
    {
        OneAtATime running = new();

        Assert.True(running.TryEnter());

        running.Leave();

        Assert.False(running.Busy);
        Assert.True(running.TryEnter());
    }

    /// <remarks>
    /// Exactly one of everything that arrives together gets in. A guard that
    /// let two through under load is one that works in every test and fails on
    /// the machine it was written for.
    /// </remarks>
    [Fact]
    public async Task OnlyOneOfManyArrivingAtOnceGetsIn()
    {
        OneAtATime running = new();

        bool[] got = await Task.WhenAll(
            Enumerable.Range(0, 64).Select(_ => Task.Run(running.TryEnter)));

        Assert.Single(got, one => one);
    }
}
