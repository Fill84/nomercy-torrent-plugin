namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// Where an episode stands, as far as this plugin is concerned.
/// </summary>
/// <remarks>
/// There is no <c>Present</c>. An episode the library has a file for is not
/// tracked at all — its row is deleted on the next refresh — so presence is the
/// absence of a row rather than a third state that could disagree with the
/// library.
/// </remarks>
/// <remarks>
/// There used to be a third state, <c>Unavailable</c>: asked for as often as
/// the profile allowed, with nothing acceptable found. It never held — every
/// maintenance pass re-derives state from the library, so the very next
/// refresh put the episode back to <see cref="Missing"/> and counted another
/// attempt regardless, which is how the owner's Freak Brothers episodes stood
/// at 67-69 attempts on 11 September 2026 while the limit read three. Asked
/// whether it should hold for a time instead, the owner chose to drop it
/// outright: every gap is searched on every run, for ever, and there is
/// nothing left for a third state to mean. Migration 009 rewrites what that
/// state left on disk.
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
    /// The library has a file, and it is one the plugin delivered from a release no name source names.
    /// </summary>
    /// <remarks>
    /// The owner's rule of 15 October 2026: such an episode is replaced on its own. It is searched for by
    /// release name as a missing one is, and it is not missing — the library has a file — so the overview
    /// does not count it as missing. South Park S29E03 was delivered that day from
    /// <c>South Park S29E03 1080p HDTV DD 2.0 x264-ViETNAM</c>, a capture with the intro missing, three hours
    /// before the name sources named a real release of it.
    /// </remarks>
    Replace,
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
    public const string Replace = "replace";

    public static string ToStored(EpisodeState state)
    {
        return state switch
        {
            EpisodeState.NotAired => NotAired,
            EpisodeState.Missing => Missing,
            EpisodeState.Replace => Replace,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No stored name for this state."),
        };
    }

    public static EpisodeState FromStored(string stored)
    {
        return stored switch
        {
            NotAired => EpisodeState.NotAired,
            Missing => EpisodeState.Missing,
            Replace => EpisodeState.Replace,
            // A row still saying 'unavailable' is one migration 009 missed —
            // every row on disk should have been rewritten before this ever
            // runs. Refusing is better than quietly filing it as missing and
            // never saying the migration did not do its job.
            _ => throw new ArgumentOutOfRangeException(nameof(stored), stored, "No such episode state."),
        };
    }
}
