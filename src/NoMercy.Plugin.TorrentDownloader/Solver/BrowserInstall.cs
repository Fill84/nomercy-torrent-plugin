using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Solver;

/// <summary>Fetching a browser, when there is not one already.</summary>
/// <remarks>
/// A seam, because the alternative is a test that downloads 150 MB — and
/// because what this slice has to prove is that it happens <em>once</em>, which
/// is a statement about how often it is called.
/// </remarks>
public interface IBrowserDownloader
{
    /// <summary>
    /// Downloads a browser into <paramref name="folder"/> and answers the path
    /// of its executable.
    /// </summary>
    Task<string> DownloadAsync(string folder, CancellationToken ct);
}

/// <summary>
/// The browser in the plugin's own data folder: downloaded once, kept across
/// restarts.
/// </summary>
/// <remarks>
/// <strong>C5.</strong> 0.3.4 re-downloaded Chrome on every server start —
/// 150 MB and a minute — because the check looked in the wrong place, and it
/// logged the download as news rather than as the fault it was. The check here
/// is for the executable it actually recorded, and the record is a file beside
/// it rather than a path guessed from a version number that moves.
/// </remarks>
public sealed class BrowserInstall(string dataFolderPath, IBrowserDownloader downloader, ILogger logger)
{
    /// <summary>Where the browser lives, inside the plugin's own data folder.</summary>
    public const string FolderName = "browser";

    /// <summary>
    /// Records the executable that was installed.
    /// </summary>
    /// <remarks>
    /// A file naming the executable, rather than working the path out again on
    /// each start. Recomputing is what went wrong before: the path a later
    /// version guessed was not the path the download had used, so the check
    /// always failed and the download always ran.
    /// </remarks>
    public const string RecordName = "installed.txt";

    private readonly string _folder = Path.Combine(dataFolderPath, FolderName);

    /// <summary>The browser's executable, downloading one only if there is none.</summary>
    public async Task<string> EnsureAsync(CancellationToken ct)
    {
        if (Installed() is string existing)
        {
            logger.LogDebug("Using the browser already in {Folder}.", _folder);

            return existing;
        }

        Directory.CreateDirectory(_folder);

        logger.LogInformation("Downloading a browser into {Folder}. This happens once.", _folder);

        string executable = await downloader.DownloadAsync(_folder, ct);

        await File.WriteAllTextAsync(Path.Combine(_folder, RecordName), executable, ct);

        DeleteHeadlessShell();

        return executable;
    }

    /// <summary>The executable recorded by a previous install, if it is still there.</summary>
    public string? Installed()
    {
        string record = Path.Combine(_folder, RecordName);

        if (!File.Exists(record))
        {
            return null;
        }

        string executable = File.ReadAllText(record).Trim();

        // The record and the file both, because a half-deleted folder is a
        // real state and answering "installed" for a browser that is not there
        // would fail later and further away.
        return File.Exists(executable) ? executable : null;
    }

    /// <summary>
    /// Removes the headless shell that ships beside the browser.
    /// </summary>
    /// <remarks>
    /// Headless is not used: measured, headless Chrome does not pass a managed
    /// challenge, and every gated source returns the interstitial for ever. The
    /// shell is deleted so nothing can quietly start the one that cannot work.
    /// </remarks>
    private void DeleteHeadlessShell()
    {
        foreach (string shell in Directory
                     .EnumerateFiles(_folder, "chrome-headless-shell*", SearchOption.AllDirectories)
                     .ToArray())
        {
            try
            {
                File.Delete(shell);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(exception, "Could not delete the headless shell at {Path}.", shell);
            }
        }
    }
}
