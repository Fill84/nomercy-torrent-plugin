namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// The release names that passed, grouped by how many of the show's wishes each carries.
/// </summary>
/// <remarks>
/// <c>docs/specs/release-names.md</c> § Choosing the release name: the names carrying the most wishes are
/// searched first; when they produce no torrent, the names carrying the next highest number, and so on down
/// to names carrying no wish. Names carrying the same number of wishes are searched together. Only groups
/// that hold a name are returned, so a wish no name carries costs nothing and leaves the show downloadable.
/// </remarks>
public static class WishGroups
{
    /// <summary>The groups, most wishes first, each in the order its names came.</summary>
    public static IReadOnlyList<IReadOnlyList<string>> Of(IEnumerable<string> names, IReadOnlyList<string> wishes)
    {
        return
        [
            .. names
                .GroupBy(name => wishes.Count(wish => NameJudge.Carries(name, wish)))
                .OrderByDescending(group => group.Key)
                .Select(group => (IReadOnlyList<string>)[.. group]),
        ];
    }
}
