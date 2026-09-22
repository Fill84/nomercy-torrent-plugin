using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// What the server says about an encode, asked by the job id it handed back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Asked, because on contract 12 there is nothing to hear.</strong> Until
/// 22 September 2026 the plugin listened to the server's own encoding events;
/// a plugin on contract 12 has no bus, and the one thing it can ask is
/// <c>IPluginJobs.StatusAsync</c>, which reads the queue's own tables for the
/// id <c>IPluginEncoder</c> handed back.
/// </para>
/// <para>
/// The mapping is the whole of what is under test, and each state is here
/// because a wrong one costs something: a running encode read as finished is a
/// download deleted under the server; a failure without its reason is a History
/// line that says nothing; an answer the server refuses read as anything but
/// unknown closes a grab on a guess.
/// </para>
/// </remarks>
public class EncoderSaysTests
{
    [Fact]
    public async Task AFinishedEncodeIsSaidToBeFinished()
    {
        FakeJobs jobs = new FakeJobs().Says("01KZGKX2G0966V80H26EKGG5T1", PluginJobState.Finished);

        EncoderSays says = new(jobs, new CapturingLogger());

        Assert.Equal(EncodeJobState.Finished, (await says.AboutAsync("01KZGKX2G0966V80H26EKGG5T1", CancellationToken.None))?.State);
        Assert.Equal(["01KZGKX2G0966V80H26EKGG5T1"], jobs.Asked);
    }

    /// <remarks>
    /// The failure is kept with its reason, because that reason is what the
    /// owner reads on the History page. "The server gave up and said no more
    /// than that" is what a grab used to be failed with.
    /// </remarks>
    [Fact]
    public async Task AFailedEncodeIsSaidToHaveFailedWithTheReason()
    {
        FakeJobs jobs = new FakeJobs().Says("job-41", PluginJobState.Failed, "the source file has no audio stream");

        EncoderSays says = new(jobs, new CapturingLogger());

        EncodeJob? standing = await says.AboutAsync("job-41", CancellationToken.None);

        Assert.Equal(EncodeJobState.Failed, standing?.State);
        Assert.Equal("the source file has no audio stream", standing?.Failure);
    }

    /// <remarks>
    /// An encode that is queued or reserved is a file the server is still going
    /// to read, or is reading. A download taken away under one is the fault that
    /// cost the owner 36 GB on 31 August 2026, so neither may read as settled.
    /// </remarks>
    [Theory]
    [InlineData(PluginJobState.Queued, EncodeJobState.Queued)]
    [InlineData(PluginJobState.Running, EncodeJobState.Running)]
    public async Task AnEncodeNotYetDoneIsSaidToBeGoing(PluginJobState server, EncodeJobState expected)
    {
        FakeJobs jobs = new FakeJobs().Says("job-77", server);

        EncoderSays says = new(jobs, new CapturingLogger());

        Assert.Equal(expected, (await says.AboutAsync("job-77", CancellationToken.None))?.State);
    }

    /// <remarks>
    /// <para>
    /// A server that will not say — the facade refuses by name, as one a host
    /// did not wire does — is answered with nothing, and nothing is never
    /// finished: Transfers leaves the staged file where it is and the library
    /// decides.
    /// </para>
    /// <para>
    /// Said once. Every pass asks about every job every grab waits on, and a
    /// refusal that repeats a line a minute buries the one line that says why.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AServerThatWillNotSayIsAnsweredWithNothingAndSaidOnce()
    {
        CapturingLogger log = new();
        FakeJobs jobs = new()
        {
            Refuses = new PluginRefusedException(
                PluginRefusalMessages.FacadeNotOnThisHost(PluginIdentity.IdText, "IPluginContext.Jobs")),
        };

        EncoderSays says = new(jobs, log);

        Assert.Null(await says.AboutAsync("job-1", CancellationToken.None));
        Assert.Null(await says.AboutAsync("job-2", CancellationToken.None));

        Assert.Single(log.Lines, line => line.Contains("would not say", StringComparison.Ordinal));
    }

    /// <remarks>
    /// The server's own word for a state it cannot place is Unknown, and this
    /// plugin has no business turning that into anything: a grab is deleted on
    /// "finished" and failed on "failed", and neither was said.
    /// </remarks>
    [Fact]
    public async Task AnUnknownStateIsAnsweredWithNothing()
    {
        FakeJobs jobs = new FakeJobs().Says("job-9", PluginJobState.Unknown);

        EncoderSays says = new(jobs, new CapturingLogger());

        Assert.Null(await says.AboutAsync("job-9", CancellationToken.None));
    }
}
