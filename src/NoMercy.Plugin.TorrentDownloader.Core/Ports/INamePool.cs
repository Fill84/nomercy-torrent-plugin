namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// One release name the feeds carried, and what it answers for.
/// </summary>
/// <param name="Key">
/// The show and the slot, normalised — see <c>PoolKey</c>. Two spellings of one
/// episode key the same, which is what makes the pool worth having.
/// </param>
/// <param name="Title">The release name exactly as the feed printed it.</param>
/// <param name="Source">Which feed carried it, for the history line.</param>
/// <param name="SeenAt">When it was harvested.</param>
public sealed record PooledName(string Key, string Title, string Source, DateTimeOffset SeenAt);

/// <summary>
/// Where harvested names are kept between one stage and the next.
/// </summary>
/// <remarks>
/// It outlives the cycle that filled it. A harvest interrupted halfway through
/// by a restart has already written what it read, so the pass that follows
/// starts from those names rather than asking every feed again.
/// </remarks>
public interface INamePool
{
    /// <summary>
    /// Keeps every one of <paramref name="names"/>, replacing a name already
    /// there under the same key and title.
    /// </summary>
    Task AddAsync(IReadOnlyList<PooledName> names, CancellationToken ct);
}
