namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Clearing up after a test that opened a database.
/// </summary>
/// <remarks>
/// <para>
/// SQLite pools its connections, so the file stays open for a while after the
/// last one is closed and the folder will not delete. The obvious answer —
/// <c>SqliteConnection.ClearAllPools()</c> — is process-wide, and test classes
/// run in parallel: one class clearing the pools disposes a connection another
/// class is in the middle of reading from, which arrives as
/// <c>ObjectDisposedException: SQLitePCL.sqlite3</c> in a test that has nothing
/// to do with it.
/// </para>
/// <para>
/// So nothing is cleared and a folder that will not go is left alone. It is
/// under the machine's temporary directory, which is what that directory is
/// for, and a stray folder there is worth less than a suite that fails once a
/// run for a reason nobody can place.
/// </para>
/// </remarks>
public static class TemporaryFolder
{
    /// <summary>Deletes it if it can, and says nothing if it cannot.</summary>
    public static void Forget(string folder)
    {
        try
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Still open, and not worth failing a green test over.
        }
    }
}
