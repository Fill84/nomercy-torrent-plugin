namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>
/// Whether an <c>Xvfb</c> is installed, found the way a shell would find it: an executable file of that name in
/// one of the folders on <c>PATH</c>.
/// </summary>
/// <remarks>
/// GitHub issue 6. Asking whether the program starts means starting it, and the failure that came back —
/// ".NET could not start 'Xvfb' with working directory /app" — named neither the program that was missing nor
/// what to do. Looking first keeps that question out of the path of every search.
/// </remarks>
public static class XvfbLookup
{
    /// <summary>The name the program is installed under. Case matters on the file systems that have it.</summary>
    private const string Name = "Xvfb";

    /// <summary>Whether <c>Xvfb</c> is on <paramref name="path"/>, as the real file system sees it.</summary>
    public static bool IsOnPath(string? path)
    {
        return IsOnPath(path, CanBeRun);
    }

    /// <summary>
    /// Whether a file called <c>Xvfb</c> is in one of the folders of <paramref name="path"/> and
    /// <paramref name="canBeRun"/> says it can be run.
    /// </summary>
    /// <remarks>
    /// Whether it can be run is the caller's to say, so the answer for a file without an execute bit can be
    /// asserted on a machine that has no such bit.
    /// </remarks>
    public static bool IsOnPath(string? path, Func<string, bool> canBeRun)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (string folder in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate;

            try
            {
                candidate = Path.Combine(folder.Trim(), Name);
            }
            catch (ArgumentException)
            {
                // A folder on PATH with a character no path can hold. The shell skips it, and so does this.
                continue;
            }

            if (File.Exists(candidate) && canBeRun(candidate))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Whether the file at <paramref name="file"/> can be run by somebody.</summary>
    /// <remarks>
    /// Windows has no execute bit, and Xvfb does not run there at all; the question is only ever put on Linux.
    /// </remarks>
    private static bool CanBeRun(string file)
    {
        return OperatingSystem.IsWindows() || CanBeRun(File.GetUnixFileMode(file));
    }

    /// <summary>Whether a file with this mode has an execute bit for its owner, its group or anyone.</summary>
    public static bool CanBeRun(UnixFileMode mode)
    {
        return (mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0;
    }
}
