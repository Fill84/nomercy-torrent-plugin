namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// The two kinds of library this plugin works in.
/// </summary>
/// <remarks>
/// Whether something is television or anime is the server's own classification,
/// made by a Kitsu-backed classifier when the file was filed. A show is already
/// in the library that matches, so its kind is the kind of the library it sits
/// in — this plugin classifies nothing and guesses nothing. Films are out of
/// scope entirely.
/// </remarks>
public enum LibraryKind
{
    Television,
    Anime,
}

/// <summary>Reading the server's library type, which is a plain string column.</summary>
public static class LibraryKinds
{
    public const string Television = "tv";
    public const string Anime = "anime";

    /// <summary>
    /// The kind a library type names, when this plugin knows it.
    /// </summary>
    /// <remarks>
    /// <c>Library.Type</c> is an indexed string with no enum behind it, so it
    /// holds whatever was written into it: compared without case, and anything
    /// unrecognised is out of scope rather than guessed at. Treating an unknown
    /// type as television would have the plugin downloading into a library
    /// nobody meant for this.
    /// </remarks>
    public static bool TryParse(string? type, out LibraryKind kind)
    {
        if (string.Equals(type, Television, StringComparison.OrdinalIgnoreCase))
        {
            kind = LibraryKind.Television;
            return true;
        }

        if (string.Equals(type, Anime, StringComparison.OrdinalIgnoreCase))
        {
            kind = LibraryKind.Anime;
            return true;
        }

        kind = default;
        return false;
    }
}
