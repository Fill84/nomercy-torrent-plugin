namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>What became of an encode this plugin asked for.</summary>
/// <remarks>
/// Queued and Running are the same thing to this plugin — not settled — and are
/// kept apart because the server keeps them apart and a page saying which is
/// worth more than a page saying neither.
/// </remarks>
public enum EncodeJobState
{
    /// <summary>Waiting its turn.</summary>
    Queued,

    /// <summary>Being encoded now.</summary>
    Running,

    /// <summary>Done, whatever the library shows.</summary>
    Finished,

    /// <summary>Given up on, with a reason.</summary>
    Failed,

    /// <summary>The server has no record of it, which a restart is enough to cause.</summary>
    Unknown,
}

/// <summary>Where one asked-for encode stands.</summary>
/// <param name="State">What the server says it is doing.</param>
/// <param name="Failure">Why it failed, in the server's own words. Null unless it did.</param>
public sealed record EncodeJob(EncodeJobState State, string? Failure);

/// <summary>
/// Asks the server what became of an encode it was asked for.
/// </summary>
/// <remarks>
/// <para>
/// Without this the plugin can see one thing only: whether the library has the
/// episode yet. So an encode that failed and one still running look the same
/// from here, and both are waited out for six hours before the grab is failed
/// and the episode goes back to missing — which is the same gigabytes
/// downloaded again for a job that died in the first minute.
/// </para>
/// <para>
/// It is media-server #31, which this plugin opened and which closed on
/// 30 August 2026. A server too old to answer has no implementation of this at
/// all, and the six-hour wait is what it falls back to.
/// </para>
/// </remarks>
public interface IEncodeJobs
{
    /// <summary>Where that job stands, or null where the server would not say.</summary>
    Task<EncodeJob?> StatusAsync(string jobId, CancellationToken ct);
}
