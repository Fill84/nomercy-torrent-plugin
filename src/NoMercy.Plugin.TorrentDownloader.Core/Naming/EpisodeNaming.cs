using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Naming;

/// <summary>Whether a release name names one particular episode of one show.</summary>
public static class EpisodeNaming
{
    /// <summary>
    /// True when <paramref name="name"/> names this show, this season and this episode, and nothing more.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>docs/specs/release-names.md</c>: a release name names one episode of one show by its season and
    /// episode number. A name without an episode number — a season pack, or an anime posted by its
    /// absolute number alone — names no episode, and neither does a name for a run of episodes.
    /// </para>
    /// <para>
    /// The show two ways, both already this plugin's. The title with its words run together, which is how
    /// <c>Frieren.Beyond.Journey.s.End</c> and <c>Frieren- Beyond Journey's End</c> come out the same: one
    /// Nyaa page carries that one episode under three spellings of what became of the apostrophe. And the
    /// title leading the name with only a year or a country after it, which is how
    /// <c>Sugar 2024 S02E01</c> names the show called Sugar.
    /// </para>
    /// </remarks>
    public static bool Names(ReleaseName name, string showTitle, EpisodeKey episode)
    {
        if (name.IsPack
            || name.Season != episode.Season
            || name.Episode != episode.Number
            || (name.LastEpisode is int last && last != name.Episode))
        {
            return false;
        }

        return IsTheShow(name, showTitle);
    }

    /// <summary>
    /// True when <paramref name="name"/> is a pack of this show's season <paramref name="season"/>, and nothing
    /// more.
    /// </summary>
    /// <remarks>
    /// <c>docs/specs/release-names.md</c> § Season packs, GitHub issue #1: a season with two or more episodes
    /// being searched for looks for its pack first. The show is matched the same two ways as an episode's.
    /// </remarks>
    public static bool NamesSeason(ReleaseName name, string showTitle, int season)
    {
        return name.IsSeasonPack && name.Season == season && IsTheShow(name, showTitle);
    }

    private static bool IsTheShow(ReleaseName name, string showTitle)
    {
        return string.Equals(TitleMatcher.Normalised(name.Title), TitleMatcher.Normalised(showTitle), StringComparison.Ordinal)
               || TitleMatcher.Matches(name.Title, showTitle);
    }
}
