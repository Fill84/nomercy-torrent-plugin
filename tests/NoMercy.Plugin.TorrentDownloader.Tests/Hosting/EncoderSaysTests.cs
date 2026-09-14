using NoMercy.Events;
using NoMercy.Events.Encoding;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// What the server says about an encode, heard rather than asked for.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plugin used to ask, once per job per tick.</strong>
/// <c>Transfers.StandingAsync</c> called <c>IEncodeJobs.StatusAsync</c> for
/// every job every grab was waiting on, every time the transfers cadence came
/// round — nine questions a minute for one season pack, for as long as its
/// encodes took. The media server has published <c>EncodingCompletedEvent</c>
/// and <c>EncodingFailedEvent</c> all along.
/// </para>
/// <para>
/// <strong>Matched on the media id, and that took checking.</strong> The event's
/// field is called <c>JobId</c> and is not one: <c>VideoEncodeJob</c> sets it
/// from <c>fileMetadata.Id</c>, which is <c>movie?.Id ?? episode!.Id</c> — the
/// row the encode registers against, and the same id this plugin hands over
/// when it asks for the encode. The id the plugin gets back from
/// <c>IPluginEncoder</c> is something else entirely: a hash of the job's
/// payload, chosen because a queue row id is not stable. Matching on that one
/// would never have found a single event.
/// </para>
/// <para>
/// The bus here is the media server's own <c>InMemoryEventBus</c>, not a stand
/// in for it.
/// </para>
/// </remarks>
public class EncoderSaysTests
{
    [Fact]
    public async Task AFinishedEncodeIsHeardWithoutBeingAskedAbout()
    {
        InMemoryEventBus bus = new();

        using EncoderSays says = new(bus, new CapturingLogger());

        Assert.Null(says.About(153823));

        await bus.PublishAsync(new EncodingCompletedEvent
        {
            JobId = 153823,
            OutputPath = "/data/tv/South Park/Season 15/episode.mkv",
            Duration = TimeSpan.FromMinutes(14),
        });

        Assert.Equal(EncodeJobState.Finished, says.About(153823)?.State);

        // And nothing is claimed about an episode nobody said anything about.
        // Null is not "finished": a pack is deleted on that answer.
        Assert.Null(says.About(153824));
    }

    /// <remarks>
    /// The failure is kept with its reason, because that reason is what the
    /// owner reads on the History page. "The server gave up and said no more
    /// than that" is what a grab used to be failed with.
    /// </remarks>
    [Fact]
    public async Task AFailedEncodeIsHeardWithTheReasonItFailed()
    {
        InMemoryEventBus bus = new();

        using EncoderSays says = new(bus, new CapturingLogger());

        await bus.PublishAsync(new EncodingFailedEvent
        {
            JobId = 41,
            InputPath = "/intake/episode.mkv",
            ErrorMessage = "the source file has no audio stream",
        });

        EncodeJob? standing = says.About(41);

        Assert.Equal(EncodeJobState.Failed, standing?.State);
        Assert.Equal("the source file has no audio stream", standing?.Failure);
    }

    /// <remarks>
    /// <para>
    /// An encode that has started and not finished is a file the server is
    /// still reading. A download taken away under one is the fault that cost
    /// the owner 36 GB on 31 August 2026, so this has to be sayable.
    /// </para>
    /// <para>
    /// And a finish after a start replaces it rather than being ignored: the
    /// two arrive in that order for every encode there is.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEncodeStillRunningIsSaidToBeRunningUntilItIsNot()
    {
        InMemoryEventBus bus = new();

        using EncoderSays says = new(bus, new CapturingLogger());

        await bus.PublishAsync(new EncodingStartedEvent
        {
            JobId = 77,
            InputPath = "/intake/episode.mkv",
            OutputPath = "/data/tv/episode.mkv",
            ProfileName = "1080p",
        });

        Assert.Equal(EncodeJobState.Running, says.About(77)?.State);

        await bus.PublishAsync(new EncodingCompletedEvent
        {
            JobId = 77,
            OutputPath = "/data/tv/episode.mkv",
            Duration = TimeSpan.FromMinutes(9),
        });

        Assert.Equal(EncodeJobState.Finished, says.About(77)?.State);
    }

    /// <remarks>
    /// <strong>This is what starts the rest of the chain.</strong> Staging,
    /// deleting the download and marking the grab done all used to wait for the
    /// transfers cadence to come round; the point of hearing the encoder is
    /// that the work happens when the encode really ended.
    /// </remarks>
    [Fact]
    public async Task TheEncoderSpeakingIsWhatSetsTheRestGoing()
    {
        InMemoryEventBus bus = new();

        using EncoderSays says = new(bus, new CapturingLogger());

        List<int> told = [];

        says.Said += media => told.Add(media);

        await bus.PublishAsync(new EncodingCompletedEvent
        {
            JobId = 153823,
            OutputPath = "/data/tv/episode.mkv",
            Duration = TimeSpan.Zero,
        });

        await bus.PublishAsync(new EncodingFailedEvent
        {
            JobId = 41,
            InputPath = "/intake/episode.mkv",
            ErrorMessage = "no audio stream",
        });

        Assert.Equal([153823, 41], told);
    }

    /// <remarks>
    /// A server that runs for months encodes thousands of files, and this is
    /// held in memory. What matters is the encodes a grab is still waiting on,
    /// which are the most recent — so the oldest are forgotten and the client
    /// falls back to the library, which is the stronger proof anyway.
    /// </remarks>
    [Fact]
    public async Task TheOldestAreForgottenRatherThanKeptForEver()
    {
        InMemoryEventBus bus = new();

        using EncoderSays says = new(bus, new CapturingLogger());

        for (int media = 1; media <= EncoderSays.Most + 50; media++)
        {
            await bus.PublishAsync(new EncodingCompletedEvent
            {
                JobId = media,
                OutputPath = "/data/tv/episode.mkv",
                Duration = TimeSpan.Zero,
            });
        }

        Assert.Null(says.About(1));
        Assert.NotNull(says.About(EncoderSays.Most + 50));
    }
}
