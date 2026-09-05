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
    /// <para>
    /// <strong>A push costs the client a whole page.</strong> The message
    /// carries the snapshot, but the host that draws a plugin does not read it:
    /// it treats any message as "something moved" and re-reads the entire view
    /// over HTTP, translations and all. That is the generic host's only option,
    /// because a payload is this plugin's own shape and the host draws every
    /// plugin.
    /// </para>
    /// <para>
    /// So the floor is not a rendering cost, it is a round trip. A download in
    /// flight moves its byte count on every tick, so the changes never stop
    /// coming, and at a quarter of a second that is four complete page reads a
    /// second — which is what the owner saw as the pages flickering.
    /// </para>
    /// </remarks>
    [Fact]
    public void AChangeEveryTenthOfASecondIsStillOnePushASecond()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);
        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        // A second of a download in flight: the byte count moves, so a change
        // is published, and it never stops for as long as the download runs.
        for (int tenth = 0; tenth < 10; tenth++)
        {
            journal.Started(ActivityStage.Find, $"tick-{tenth}");
            live.Changed();
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }

        Assert.Single(hub.Pushes);
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
        Assert.Equal(2, payload.InFlight!.Count);
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

    /// <remarks>
    /// <para>
    /// <strong>A push says that something moved. It does not have to carry the
    /// last five hundred things that moved.</strong> The payload carried the
    /// journal's whole history — capped at five hundred events, roughly a
    /// hundred kilobytes — on every push, about once a second while anything is
    /// downloading, to every open page.
    /// </para>
    /// <para>
    /// Nothing reads it. Not this plugin, and by this class's own contract not
    /// the host either: any message means "something moved" and the host
    /// answers by re-reading the whole view over HTTP. So it was a hundred
    /// kilobytes a second of postage on an empty envelope.
    /// </para>
    /// </remarks>
    [Fact]
    public void AHundredKilobytesOfHistoryIsNotSentAtAll()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);

        for (int one = 0; one < 200; one++)
        {
            journal.Finished(ActivityStage.Find, $"something {one}", "done");
        }

        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        (string Type, object? Payload) sent = Assert.Single(hub.Pushes);
        LiveSnapshot.Payload payload = Assert.IsType<LiveSnapshot.Payload>(sent.Payload);

        // Two hundred events came and went, and the push carries none of them:
        // there is no property that could hold a history, and nothing was left
        // in flight to report. All it says is that something moved, and when —
        // which is all the host does anything with.
        Assert.Null(payload.InFlight);
        Assert.NotEqual(default, payload.At);
    }

    /// <remarks>
    /// <para>
    /// <strong>A push carries what changed, and nothing else.</strong> It used
    /// to carry the whole state every time — the work in flight and the cycle,
    /// changed or not — so a torrent moving its byte count re-sent a list of
    /// jobs that had not moved since the page was opened.
    /// </para>
    /// <para>
    /// The owner asked for two things and got one: push when something changes,
    /// and push only the changes. This is the second.
    /// </para>
    /// </remarks>
    [Fact]
    public void APushCarriesOnlyThePartsThatChanged()
    {
        FakeTimeProvider clock = new();
        FakeHub hub = new();
        ActivityJournal journal = new(clock);
        using LiveSnapshot live = new(hub, journal, new CapturingLogger(), () => CycleStatus.Unknown, clock);

        journal.Started(ActivityStage.Find, "Silo S03E06");

        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        LiveSnapshot.Payload first = Assert.IsType<LiveSnapshot.Payload>(hub.Pushes[0].Payload);

        // The first push has to say everything: the page has nothing yet.
        Assert.NotNull(first.InFlight);
        Assert.NotNull(first.Cycle);

        // Nothing has moved since. The cycle is the same object and the work in
        // flight is the same work.
        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        LiveSnapshot.Payload second = Assert.IsType<LiveSnapshot.Payload>(hub.Pushes[1].Payload);

        Assert.Null(second.InFlight);
        Assert.Null(second.Cycle);

        // And the moment something does move, it is in the message again.
        journal.Finished(ActivityStage.Find, "Silo S03E06", "one copy");

        live.Changed();
        clock.Advance(LiveSnapshot.MinimumInterval);

        LiveSnapshot.Payload third = Assert.IsType<LiveSnapshot.Payload>(hub.Pushes[2].Payload);

        Assert.NotNull(third.InFlight);
    }
}
