using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The server's jobs facade, answering what a test told it to about each job id.
/// </summary>
/// <remarks>
/// A job the test said nothing about is answered as the server answers one in
/// neither of its tables: Finished, because a plugin only holds an id the server
/// handed it, so a row that is gone is a job that ran. That is the server's own
/// rule (<c>PluginJobs.StatusAsync</c>), and a fake that answered null instead
/// would pass a plugin that treats "gone" as "unknown" and never closes a grab.
/// </remarks>
public sealed class FakeJobs : IPluginJobs
{
    private readonly Dictionary<string, PluginJobStatus> _said = new(StringComparer.Ordinal);

    /// <summary>Every job id it was asked about, in order.</summary>
    public List<string> Asked { get; } = [];

    /// <summary>Refuses every question with this, as a facade the server did not wire does.</summary>
    public PluginRefusedException? Refuses { get; set; }

    public FakeJobs Says(string jobId, PluginJobState state, string? failure = null)
    {
        _said[jobId] = new(jobId, state, failure, state is PluginJobState.Finished or PluginJobState.Failed ? DateTimeOffset.UtcNow : null);

        return this;
    }

    public Task<PluginJobStatus?> StatusAsync(string jobId, CancellationToken ct = default)
    {
        Asked.Add(jobId);

        if (Refuses is not null)
        {
            throw Refuses;
        }

        return Task.FromResult<PluginJobStatus?>(
            _said.GetValueOrDefault(jobId) ?? new(jobId, PluginJobState.Finished, null, null));
    }
}
