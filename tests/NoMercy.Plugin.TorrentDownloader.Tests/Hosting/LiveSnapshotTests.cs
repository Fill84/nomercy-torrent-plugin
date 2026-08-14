using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

public class LiveSnapshotTests
{
    /// <remarks>
    /// A cycle publishes constantly — every source answering, every episode
    /// moving a stage — and each push is a message to every open page. Without
    /// coalescing, the pages spend a cycle re-rendering faster than a person
    /// can read, and the server carries the traffic.
    /// </remarks>
    [Fact]
    public void TenChangesInAHundredMillisecondsAreOnePush()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);
        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        for (int index = 0; index < 10; index++)
        {
            journal.Started(ActivityStage.Find, $"episode-{index}");
            live.Changed();
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }

        Assert.Empty(hub.Pushes);

        clock.Advance(LiveSnapshot.MinimumInterval);

        Assert.Single(hub.Pushes);
        Assert.Equal(LiveSnapshot.Channel, hub.Pushes[0].Type);
    }

    /// <remarks>
    /// Coalesced, not dropped. The last change in a burst is the one that
    /// matters, and a page left showing the state from before it would be
    /// wrong until something else happened to move.
    /// </remarks>
    [Fact]
    public void ThePushCarriesTheStateAfterTheLastChange()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);
        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        journal.Started(ActivityStage.Grab, "Silo S03E06");
        live.Changed();
        journal.Started(ActivityStage.Grab, "Lioness S03E01");
        live.Changed();

        clock.Advance(LiveSnapshot.MinimumInterval);

        LiveSnapshot.Payload payload = Assert.IsType<LiveSnapshot.Payload>(hub.Pushes[0].Payload);
        Assert.Equal(2, payload.Activity.InFlight.Count);
    }

    /// <remarks>
    /// The hub throws when a client has gone — a browser tab closed mid-push is
    /// ordinary. This runs on a timer, so an escaping exception has no caller
    /// to catch it and takes the process down: the media server, not the
    /// plugin. A page that missed one push is repaired by the next one.
    /// </remarks>
    [Fact]
    public void APushThatThrowsDoesNotEscapeAndDoesNotStopTheNextOne()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new() { Throws = new InvalidOperationException("no such client") };
        ActivityJournal journal = new(clock);
        CapturingLogger log = new();
        using LiveSnapshot live = new(hub, journal, log, () => CycleStatus.Unknown, clock);

        journal.Started(ActivityStage.Find, "Silo S03E06");
        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        Assert.Contains(log.Lines, line => line.Contains("push", StringComparison.OrdinalIgnoreCase));

        hub.Throws = null;
        journal.Started(ActivityStage.Find, "Lioness S03E01");
        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        Assert.Single(hub.Pushes);
    }

    /// <remarks>
    /// Quiet means quiet. A ticker that pushed an unchanged snapshot every
    /// quarter second would be the poll this design exists to avoid.
    /// </remarks>
    [Fact]
    public void NothingChangingPushesNothing()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        using LiveSnapshot live = new(hub, new ActivityJournal(clock), new CapturingLogger(), () => CycleStatus.Unknown, clock);

        clock.Advance(TimeSpan.FromSeconds(30));

        Assert.Empty(hub.Pushes);
    }

    /// <remarks>
    /// And it goes quiet again afterwards. The interval is a floor between
    /// pushes, not a heartbeat: a timer left repeating would push the same
    /// snapshot four times a second for as long as the server stayed up, and
    /// the first change of the day would be what started it.
    /// </remarks>
    [Fact]
    public void AfterTheBurstItGoesQuietAgain()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);
        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        journal.Started(ActivityStage.Find, "Silo S03E06");
        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);
        Assert.Single(hub.Pushes);

        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Single(hub.Pushes);
    }
}
