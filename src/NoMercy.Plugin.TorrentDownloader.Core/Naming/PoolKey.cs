namespace NoMercy.Plugin.TorrentDownloader.Core.Naming;

/// <summary>
/// What a harvested name answers for: a show and a slot, spelled one way.
/// </summary>
/// <remarks>
/// The pool is filled from feeds, which know nothing about this library, and
/// read by the resolver, which knows nothing about those feeds. The key is what
/// joins them, so both sides have to arrive at the same string from very
/// different starting points — <c>Silo.S03E06.1080p.WEB.H264-CAKES</c> from one
/// and show 1399 season 3 episode 6 from the other.
/// </remarks>
public static class PoolKey
{
    /// <summary>One episode of one season.</summary>
    public static string For(string showTitle, int season, int episode)
    {
        return $"{TitleMatcher.Normalised(showTitle)}|s{season:00}e{episode:00}";
    }

    /// <summary>
    /// A whole season, which is what a pack answers for.
    /// </summary>
    /// <remarks>
    /// Kept apart from the episodes of that season rather than expanded into
    /// them: a pack answers for every gap, and which gaps those are is not
    /// known to a stage reading a feed.
    /// </remarks>
    public static string ForSeason(string showTitle, int season)
    {
        return $"{TitleMatcher.Normalised(showTitle)}|s{season:00}";
    }

    /// <summary>
    /// An episode counted from the start of the programme, which is how anime
    /// is posted and never how television is.
    /// </summary>
    public static string ForAbsolute(string showTitle, int absolute)
    {
        return $"{TitleMatcher.Normalised(showTitle)}|a{absolute}";
    }

    /// <summary>
    /// What <paramref name="name"/> answers for, or null when it answers for
    /// nothing.
    /// </summary>
    /// <remarks>
    /// Null is the ordinary answer for half a scene feed: those carry films,
    /// and a film has no slot. Keying one under its title alone would leave it
    /// in the pool for ever, compared against every episode of every show and
    /// matching none of them.
    /// </remarks>
    public static string? Of(ReleaseName name)
    {
        if (name.Title.Length == 0)
        {
            return null;
        }

        if (name.Season is int season)
        {
            return name.Episode is int episode
                ? For(name.Title, season, episode)
                : ForSeason(name.Title, season);
        }

        return name.Absolute is int absolute ? ForAbsolute(name.Title, absolute) : null;
    }
}
