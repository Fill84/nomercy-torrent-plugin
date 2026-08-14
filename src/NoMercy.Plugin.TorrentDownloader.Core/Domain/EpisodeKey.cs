namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Which episode, of which show.
/// </summary>
/// <remarks>
/// A struct with value equality, so it can key a dictionary and be compared
/// without a thought. The show id is part of it: season 1 episode 1 is not one
/// episode, it is one per show, and a key that left the show out would have two
/// shows' first episodes collapse into each other in any map built from it.
/// </remarks>
/// <param name="ShowId">The provider's show id, which the whole contract keys on.</param>
/// <param name="Season">Season 0 is specials.</param>
/// <param name="Number">The episode's number within that season.</param>
public readonly record struct EpisodeKey(int ShowId, int Season, int Number)
{
    /// <summary>Season 0, which is skipped unless the owner asked for specials.</summary>
    public bool IsSpecial => Season == 0;

    public override string ToString()
    {
        return $"S{Season:00}E{Number:00}";
    }
}
