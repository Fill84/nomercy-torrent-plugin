using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The server's own answer about a job, in this plugin's words.
/// </summary>
/// <remarks>
/// media-server #31. One method, and the only thing between the plugin and
/// knowing whether an encode it asked for is running, finished or dead.
/// </remarks>
public sealed class HostEncodeJobs(IPluginJobs jobs) : IEncodeJobs
{
    public async Task<EncodeJob?> StatusAsync(string jobId, CancellationToken ct)
    {
        PluginJobStatus? status = await jobs.StatusAsync(jobId, ct).ConfigureAwait(false);

        if (status is null)
        {
            return null;
        }

        return new(
            status.State switch
            {
                PluginJobState.Queued => EncodeJobState.Queued,
                PluginJobState.Running => EncodeJobState.Running,
                PluginJobState.Finished => EncodeJobState.Finished,
                PluginJobState.Failed => EncodeJobState.Failed,

                // Including anything a later server adds. A state this plugin
                // does not know is not a state to act on, and treating it as
                // failed would throw away a job that is running.
                _ => EncodeJobState.Unknown,
            },
            status.Failure);
    }
}
