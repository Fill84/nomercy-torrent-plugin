using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// Where a decision is written down, the moment it is made.
/// </summary>
/// <remarks>
/// <para>
/// A cycle used to answer with a report and the caller wrote the whole of it
/// afterwards, so nothing existed until every episode had been looked at. Over
/// twenty-eight gaps that is half an hour in which the pages say nothing and
/// the owner cannot see what is being decided — and a run stopped, cancelled or
/// crashed in that time threw away everything it had worked out.
/// </para>
/// <para>
/// One episode, one write, as it happens. The report still comes back at the
/// end for whatever wants the whole of it, but nothing depends on reaching the
/// end any more.
/// </para>
/// </remarks>
public interface ICycleJournal
{
    /// <summary>
    /// One episode has been decided.
    /// </summary>
    /// <param name="outcome">What became of it.</param>
    /// <param name="refused">What was refused for it, and why.</param>
    /// <param name="ct">The plugin's own lifetime.</param>
    Task DecidedAsync(EpisodeOutcome outcome, IReadOnlyList<SkippedRelease> refused, CancellationToken ct);
}
