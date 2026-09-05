namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Taking a test's temporary folder away once nothing is writing into it.
/// </summary>
/// <remarks>
/// <para>
/// A run makes its files when its session opens, and its loops keep going for a
/// moment after the test that made it has returned. Deleting the folder from
/// under that races: on Linux the delete throws <c>Directory not empty</c> when
/// a file appears between the walk and the removal, and the test is reported
/// failed for something that happened after it had already passed.
/// </para>
/// <para>
/// It cost a release. Three runs of CI on 5 September 2026 went red on a suite
/// that was green, and the same suite passed on Windows every time, because
/// there the timing usually falls the other way.
/// </para>
/// </remarks>
public static class TempFolder
{
    /// <summary>
    /// Deletes it, waiting a moment for whatever is still writing.
    /// </summary>
    /// <remarks>
    /// Given up on rather than thrown: a temporary folder that outlives a test
    /// run is the operating system's to clear, and failing a suite over one is
    /// reporting a fault that is not there.
    /// </remarks>
    public static void Clear(string folder)
    {
        for (int attempt = 0; attempt < 20 && Directory.Exists(folder); attempt++)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception busy) when (busy is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
    }
}
