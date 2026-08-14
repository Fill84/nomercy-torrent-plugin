namespace NoMercy.Plugin.TorrentDownloader.Core.Sources;

/// <summary>
/// What a source can answer. A source may be more than one thing.
/// </summary>
/// <remarks>
/// Flags rather than a single value because a feed that also takes a search is
/// genuinely both, and forcing a choice would either stop it being read whole
/// or stop it being asked a question.
/// </remarks>
[Flags]
public enum SourceRole
{
    None = 0,

    /// <summary>
    /// Answers what was released recently. Asked nothing and read whole: a feed
    /// answers any question with the newest N posts, so putting one in the
    /// search set makes an identical request per episode.
    /// </summary>
    Feed = 1,

    /// <summary>
    /// Answers what a release is called, asked show and slot. Never answers
    /// torrents, and that is the point: only a full release name is fit to put
    /// to an indexer.
    /// </summary>
    Names = 2,

    /// <summary>
    /// Answers who is serving a named release, asked the full release name.
    /// Answers rows with hashes.
    /// </summary>
    Indexer = 4,
}

/// <summary>
/// Which roles a source has, decided from its kind and whether it has a search
/// address — and by nothing else.
/// </summary>
/// <remarks>
/// Nothing guesses. 0.3.4 put a feed with no search into the search set and
/// made forty identical requests a cycle, each one the same newest-first page.
/// </remarks>
public static class SourceRoles
{
    /// <summary>Kinds that answer what was released recently.</summary>
    private static readonly HashSet<string> Feeds =
        new(StringComparer.OrdinalIgnoreCase) { "rss", "eztv-api" };

    /// <summary>Kinds that answer what a release is called, and nothing else.</summary>
    private static readonly HashSet<string> Names =
        new(StringComparer.OrdinalIgnoreCase) { "srrdb" };

    /// <summary>Kinds that answer who is serving a release.</summary>
    private static readonly HashSet<string> Indexers =
        new(StringComparer.OrdinalIgnoreCase) { "apibay", "site", "torrent-rss", "yts", "torznab" };

    /// <summary>
    /// The roles a source of this kind has.
    /// </summary>
    /// <remarks>
    /// A feed with a search address is also a name database — that is the only
    /// case where the address changes the answer. An unknown kind has no role
    /// at all rather than a guessed one: a source nobody can place is one the
    /// health tool should flag, not one silently asked the wrong question.
    /// </remarks>
    public static SourceRole For(string? kind, bool hasSearchAddress)
    {
        if (kind is null)
        {
            return SourceRole.None;
        }

        if (Feeds.Contains(kind))
        {
            return hasSearchAddress ? SourceRole.Feed | SourceRole.Names : SourceRole.Feed;
        }

        if (Names.Contains(kind))
        {
            return SourceRole.Names;
        }

        return Indexers.Contains(kind) ? SourceRole.Indexer : SourceRole.None;
    }
}
