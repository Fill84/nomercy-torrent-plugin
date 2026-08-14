namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Where an episode stands, as far as this plugin is concerned.
/// </summary>
/// <remarks>
/// There is no <c>Present</c>. An episode the library has a file for is not
/// tracked at all — its row is deleted on the next refresh — so presence is the
/// absence of a row rather than a fourth state that could disagree with the
/// library.
/// </remarks>
public enum EpisodeState
{
    /// <summary>
    /// No air date, or one in the future. Never searched and never counted as
    /// missing: looking for an episode that has not aired finds either nothing
    /// or something that should not exist.
    /// </summary>
    NotAired,

    /// <summary>Aired, and the library has no file. This is the work.</summary>
    Missing,

    /// <summary>
    /// Asked for as often as the profile allows, and nothing acceptable exists.
    /// </summary>
    /// <remarks>
    /// Given up for now, never permanently. Every maintenance pass re-derives
    /// state from the library, so an episode here goes back to
    /// <see cref="Missing"/> and is tried again — 0.3.4 filtered this state out
    /// of the refresh and preserved it, and an episode that went unavailable
    /// once was invisible for ever.
    /// </remarks>
    Unavailable,
}

/// <summary>The state names as they are stored, which are not the C# names.</summary>
/// <remarks>
/// Written out rather than taken from <c>ToString</c>: renaming a member of an
/// enum would otherwise silently rewrite what is in the database, and every row
/// written before the rename would stop matching.
/// </remarks>
public static class EpisodeStates
{
    public const string NotAired = "notaired";
    public const string Missing = "missing";
    public const string Unavailable = "unavailable";

    public static string ToStored(EpisodeState state)
    {
        return state switch
        {
            EpisodeState.NotAired => NotAired,
            EpisodeState.Missing => Missing,
            EpisodeState.Unavailable => Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No stored name for this state."),
        };
    }

    public static EpisodeState FromStored(string stored)
    {
        return stored switch
        {
            NotAired => EpisodeState.NotAired,
            Missing => EpisodeState.Missing,
            Unavailable => EpisodeState.Unavailable,
            _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "No such episode state."),
        };
    }
}
