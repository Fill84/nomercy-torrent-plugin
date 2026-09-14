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
/// What the server has said about an encode, without having been asked.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This replaced a question asked once a job once a tick.</strong> The
/// plugin held a job id for every episode it had dispatched and asked the
/// server about each of them on every transfers pass — nine questions a minute
/// for one season pack, for as long as its encodes took, and the cadence was a
/// minute precisely so that a finished encode was not noticed much later than
/// it happened. The server publishes what it is doing; there was never anything
/// to ask.
/// </para>
/// <para>
/// <strong>Keyed by the media id, never by a job id.</strong> The id the plugin
/// gets back when it asks for an encode is a hash of the job's payload — chosen
/// deliberately, because a queue row id is not stable: a finished job is deleted
/// and a failed one is rewritten under a new identity. What the server's own
/// events carry is the row the encode registers its result against, which is
/// the episode or film id the plugin named when it asked. That is the only
/// thing the two ends have in common.
/// </para>
/// <para>
/// <strong>Null is not "finished".</strong> It means nothing has been said, and
/// a plugin that read it as finished would delete a download the server was
/// still reading. Where nothing has been said the library is the proof, and it
/// is the stronger of the two.
/// </para>
/// </remarks>
public interface IEncoderSays
{
    /// <summary>What has been said about the encode for one media row, or null.</summary>
    EncodeJob? About(int mediaId);
}
