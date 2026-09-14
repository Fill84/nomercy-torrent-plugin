using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Whether anybody is looking at this plugin's pages, known without asking.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The flood the owner saw.</strong> A running download changes what the
/// Downloads page draws every second, every change was a push, and every push
/// makes <c>PluginScreen.vue</c> fetch the whole view again over HTTP. That was
/// a page load a second per open tab — and the same work, pushed to nobody, on
/// a server with no tab open at all.
/// </para>
/// <para>
/// <strong>The hub cannot say who is watching.</strong>
/// <c>PluginHub.Subscribe</c> adds a connection to <c>plugin:{ulid}</c> and tells
/// the plugin nothing. It does not need to: <strong>a page being fetched is the
/// proof</strong>, and a page that is open fetches itself again on every push —
/// so a push that nothing fetches after is a push nobody saw.
/// </para>
/// </remarks>
public class OnlookersTests
{
    [Fact]
    public void NobodyIsLookingUntilAPageIsFetched()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        Assert.False(onlookers.Present);

        onlookers.Looked();

        Assert.True(onlookers.Present);
    }

    /// <remarks>
    /// The ordinary case, and the one that must go on for as long as a tab is
    /// open: something changes, the page is told, the page fetches itself, and
    /// that fetch is the answer.
    /// </remarks>
    [Fact]
    public void APushThePageAnswersKeepsItLooked()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        onlookers.Looked();

        for (int push = 0; push < 20; push++)
        {
            onlookers.Told();

            clock.Advance(TimeSpan.FromSeconds(1));

            onlookers.Looked();

            clock.Advance(Onlookers.Answer);
        }

        Assert.True(onlookers.Present);
    }

    /// <remarks>
    /// <strong>The tab was closed.</strong> Nothing says so — the socket may stay
    /// up for a dashboard on another page — but the next push goes unanswered,
    /// and that is enough. From here nothing is sampled and nothing is pushed on
    /// behalf of a page that is not there.
    /// </remarks>
    [Fact]
    public void APushNobodyAnswersMeansNobodyIsLooking()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        int left = 0;

        onlookers.Left += () => left++;

        onlookers.Looked();
        onlookers.Told();

        clock.Advance(Onlookers.Answer);

        Assert.False(onlookers.Present);
        Assert.Equal(1, left);
    }

    /// <remarks>
    /// <para>
    /// <strong>A page open on nothing moving.</strong> No change means no push,
    /// and no push means no unanswered one — so without this a closed tab over a
    /// list that never changes would have the client read once a second for as
    /// long as the server ran. After <see cref="Onlookers.Idle"/> of nothing, the
    /// looking rests.
    /// </para>
    /// <para>
    /// And the client stirring brings it back, because resting is not leaving:
    /// the page may well be open, and a stalled torrent that starts again is the
    /// one the owner was staring at. `S11-29`.
    /// </para>
    /// </remarks>
    [Fact]
    public void NothingChangingForLongRestsAndTheClientStirringWakesIt()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        int arrived = 0;

        onlookers.Arrived += () => arrived++;

        onlookers.Looked();

        Assert.Equal(1, arrived);

        clock.Advance(Onlookers.Idle);

        Assert.False(onlookers.Present);

        onlookers.Stirred();

        Assert.True(onlookers.Present);
        Assert.Equal(2, arrived);
    }

    /// <remarks>
    /// A tab that did not answer is gone, and the client moving is no reason to
    /// believe it came back. Only a page being fetched is.
    /// </remarks>
    [Fact]
    public void AClientStirringDoesNotBringBackAPageThatLeft()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        onlookers.Looked();
        onlookers.Told();

        clock.Advance(Onlookers.Answer);

        onlookers.Stirred();

        Assert.False(onlookers.Present);

        // A page fetched again is.
        onlookers.Looked();

        Assert.True(onlookers.Present);
    }

    /// <remarks>
    /// Nobody has ever looked, so a stirring client is nobody's business: a
    /// server with no tab open does no page work at all.
    /// </remarks>
    [Fact]
    public void AClientStirringBeforeAnyoneLookedStartsNothing()
    {
        FakeTimeProvider clock = new();

        using Onlookers onlookers = new(clock);

        int arrived = 0;

        onlookers.Arrived += () => arrived++;

        onlookers.Stirred();

        Assert.False(onlookers.Present);
        Assert.Equal(0, arrived);
    }
}
