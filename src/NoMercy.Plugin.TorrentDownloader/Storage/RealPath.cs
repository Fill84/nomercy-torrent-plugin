namespace NoMercy.Plugin.TorrentDownloader.Storage;

/// <summary>A path with every symbolic link in it followed, as SQLite reports a database's path.</summary>
/// <remarks>
/// SQLite on Linux and macOS resolves every link in a database's path, so
/// <c>PRAGMA database_list</c> names the real file; <see cref="Path.GetFullPath(string)"/>
/// resolves none. The store compares the two to be sure the host opened the
/// plugin's own database, and compared as written a data folder under a link —
/// <c>/var</c> on macOS is one, and so is a data folder moved to a bigger disk —
/// was refused on every open although it was the right file.
/// </remarks>
public static class RealPath
{
    /// <summary>How many links are followed before giving up, as SQLite does for a loop.</summary>
    private const int MostLinks = 100;

    public static string Of(string path)
    {
        int followed = 0;

        return Of(Path.GetFullPath(path), ref followed);
    }

    private static string Of(string full, ref int followed)
    {
        string root = Path.GetPathRoot(full) ?? string.Empty;
        string resolved = root;

        foreach (string part in full[root.Length..].Split(
                     [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                     StringSplitOptions.RemoveEmptyEntries))
        {
            string next = Path.Combine(resolved, part);
            FileSystemInfo here = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);

            // A part that does not exist yet, the database file on a fresh
            // install, is taken as written: there is no link in it to follow.
            if (here.LinkTarget is null || followed++ >= MostLinks
                || here.ResolveLinkTarget(returnFinalTarget: true) is not FileSystemInfo target)
            {
                resolved = next;

                continue;
            }

            // The folder a link points at can itself sit under a link.
            resolved = Of(Path.GetFullPath(target.FullName), ref followed);
        }

        return resolved;
    }
}
