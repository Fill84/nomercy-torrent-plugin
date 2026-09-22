using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// What the server says about an encode, asked through <see cref="IPluginJobs"/>
/// by the id it handed back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Asked, because there is nothing to hear.</strong> Until contract 12
/// this listened to the server's own encoding events and a transfers pass ran
/// the moment one arrived. A plugin on contract 12 has no bus: the context
/// carries <see cref="IPluginJobs"/>, which reads the queue's own tables for the
/// job id <see cref="IPluginEncoder"/> handed back, and that is what this asks.
/// </para>
/// <para>
/// <strong>And what it reads is the better answer.</strong> A failed event was
/// not the end of a job — the server put it back with a back-off and said
/// nothing when the last attempt moved it to the failed table — so the plugin
/// could never close on one. The failed table is what the facade reads, so a
/// job is failed here only once the server has truly given it up, with the
/// reason written there. A job in neither table ran and was cleared, which the
/// server says as Finished; one still queued or reserved is not settled.
/// </para>
/// <para>
/// A facade that refuses — a server that mediates no jobs after all, or one that
/// took the encoder hook back — answers null, said once in the log. Null is not
/// "finished": Transfers reads it as no proof either way and leaves the staged
/// file where it is.
/// </para>
/// </remarks>
public sealed class EncoderSays(IPluginJobs jobs, ILogger logger) : IEncoderSays
{
    private int _refused;

    public async Task<EncodeJob?> AboutAsync(string jobId, CancellationToken ct)
    {
        try
        {
            PluginJobStatus? status = await jobs.StatusAsync(jobId, ct).ConfigureAwait(false);

            return status?.State switch
            {
                PluginJobState.Queued => new(EncodeJobState.Queued, null),
                PluginJobState.Running => new(EncodeJobState.Running, null),
                PluginJobState.Finished => new(EncodeJobState.Finished, null),
                PluginJobState.Failed => new(EncodeJobState.Failed, status.Failure),

                // Unknown, or no answer at all: nothing can be said, and saying
                // "finished" instead is how a download the server is still
                // reading gets deleted.
                _ => null,
            };
        }
        catch (PluginRefusedException refused)
        {
            // Once. Every pass asks about every job a grab waits on, and a refusal
            // that repeats a line a minute buries the one line that says why.
            if (Interlocked.Exchange(ref _refused, 1) == 0)
            {
                logger.LogWarning(
                    "The server would not say what became of an encode: {Reason} No encode is closed on that, and the library decides.",
                    refused.Message);
            }

            return null;
        }
    }
}
