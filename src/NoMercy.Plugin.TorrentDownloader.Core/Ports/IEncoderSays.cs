namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>What became of an encode this plugin asked the server for.</summary>
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
}

/// <summary>Where one asked-for encode stands.</summary>
/// <param name="State">What the server has said it is doing.</param>
/// <param name="Failure">Why it failed, in the server's own words. Null unless it did.</param>
public sealed record EncodeJob(EncodeJobState State, string? Failure);

/// <summary>
/// What the server says about an encode, asked by the id it handed back.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Asked, once a job once a pass — and this is the second time it has
/// been that.</strong> The plugin asked this way first; then, on 14 September
/// 2026, it heard the server's own encoding events instead, because those carry
/// the media row an encode registers against and arrive the moment it ends. A
/// plugin on contract 12 has no bus to hear them on. What it has is
/// <c>IPluginJobs</c>, which answers for the job id <c>IPluginEncoder</c> hands
/// back and for nothing else — so that id is kept with the grab, and this asks
/// by it.
/// </para>
/// <para>
/// <strong>And the answer is better than the events were.</strong> A failed
/// event was not the end of a job: the server put it back with a back-off and
/// said nothing when the last attempt moved it to the failed table. The queue's
/// tables are what this reads, so a job is failed here only once the server has
/// truly given it up, and its reason is the one written there.
/// </para>
/// <para>
/// <strong>Null is not "finished".</strong> It means nothing can be said — no
/// id was kept, or the server would not answer — and a plugin that read it as
/// finished would delete a download the server was still reading. Where nothing
/// can be said the library is the proof, and it is the stronger of the two.
/// </para>
/// </remarks>
public interface IEncoderSays
{
    /// <summary>Where the encode the server called <paramref name="jobId"/> stands, or null.</summary>
    Task<EncodeJob?> AboutAsync(string jobId, CancellationToken ct);
}
