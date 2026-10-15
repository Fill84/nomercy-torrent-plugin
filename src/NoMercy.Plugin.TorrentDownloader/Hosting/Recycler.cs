using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Puts the encoded files of an episode the plugin delivered into the server's recycle area.
/// </summary>
/// <remarks>
/// <para>
/// The owner's rule of 15 October 2026: a wrong episode the plugin delivered is replaced on its own, and the
/// old one goes to the server's recycle area, where the owner can still get it back. Never deleted: the
/// plugin recycles through <see cref="IPluginLibraryWriter.RecycleAsync"/>, the call the contract names for "a
/// quality upgrade that replaces an episode", and asks nothing else of the writer.
/// </para>
/// <para>
/// <strong>A file at a time.</strong> The server moves what it is handed with a file move, which takes no
/// folder, into a <c>.recycled</c> folder beside it (<c>PluginLibraryWriter.RecyclePathFor</c>). A file
/// already in such a folder is never handed over again.
/// </para>
/// </remarks>
/// <param name="writer">
/// The server's library writer: <c>IPluginContext.LibraryWriter</c>, null where the owner granted the plugin no
/// library to write in.
/// </param>
public sealed class Recycler(IPluginLibraryWriter? writer)
{
    /// <summary>The folder the server puts a recycled file in, beside where it was.</summary>
    public const string RecycleFolder = ".recycled";

    /// <summary>
    /// Recycles the files of <paramref name="encoded"/> that the plugin's delivery caused; null once they are
    /// all in the recycle area, otherwise why they are not.
    /// </summary>
    /// <param name="encoded">The episode's folder and the file the library names in it.</param>
    /// <param name="from">When the delivery was taken on: nothing older is the delivery's.</param>
    /// <param name="until">When the replacement was taken on: nothing newer is the old delivery's.</param>
    /// <param name="ct">The plugin's lifetime.</param>
    /// <remarks>
    /// <para>
    /// <strong>Only what the delivery caused.</strong> A file written before its grab was taken on was there
    /// before the plugin's encode and is not the plugin's; one written after the replacement was taken on is
    /// not the old delivery's. The file the library names is the proof of which: when it is older than the
    /// delivery, the episode in the library is not the one the plugin delivered, and nothing is recycled.
    /// </para>
    /// <para>
    /// <strong>The file the library names goes first.</strong> Once it is gone the server encodes the episode
    /// afresh whatever else is left — its reconciler reads a missing master playlist as an output to write in
    /// full — so a recycling that stops half way can never leave an encode skipped over a half-moved episode.
    /// Where it is already in the folder's recycle area, an earlier look recycled it and stopped, and the rest
    /// is carried on with.
    /// </para>
    /// </remarks>
    public async Task<string?> RecycleAsync(EncodedEpisode encoded, DateTimeOffset from, DateTimeOffset until, CancellationToken ct)
    {
        if (writer is null)
        {
            return "the owner has granted the plugin no library to write in";
        }

        List<string> files;

        try
        {
            if (!Directory.Exists(encoded.Folder))
            {
                return $"the folder {encoded.Folder} is not there";
            }

            files =
            [
                .. Directory.EnumerateFiles(encoded.Folder, "*", SearchOption.AllDirectories)
                    .Select(path => path.Replace('\\', '/'))
                    .Where(path => !InRecycleArea(encoded.Folder, path)),
            ];
        }
        catch (Exception unreadable) when (unreadable is IOException or UnauthorizedAccessException)
        {
            return $"the folder {encoded.Folder} could not be read: {unreadable.Message}";
        }

        string? named = files.FirstOrDefault(path => string.Equals(path, encoded.File, StringComparison.OrdinalIgnoreCase));

        if (named is null && !File.Exists(Path.Combine(encoded.Folder, RecycleFolder, Path.GetFileName(encoded.File))))
        {
            return $"the file the library names for it, {encoded.File}, is not there";
        }

        if (named is not null && !Caused(named, from, until))
        {
            return $"its file in the library, {Path.GetFileName(named)}, was written before the plugin's delivery, so it is not the plugin's";
        }

        string[] ours =
        [
            .. files
                .Where(path => Caused(path, from, until))
                .OrderBy(path => string.Equals(path, named, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(path => path, StringComparer.Ordinal),
        ];

        if (ours.Length == 0)
        {
            return null;
        }

        try
        {
            // Asked once, before anything moves, so a library the plugin may not write in moves nothing at all.
            if (!await writer.CanWriteAsync(ours[0], ct))
            {
                return "the server does not let the plugin write in the library this episode is in";
            }

            foreach (string path in ours)
            {
                await writer.RecycleAsync(path, ct);
            }
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            // PluginLibraryAccessDeniedException in process, PluginRefusedException out of it, and whatever
            // the server's storage throws. Whichever it is, what is left stays and the next look carries on.
            return $"the server refused: {refused.Message}";
        }

        return null;
    }

    /// <summary>Whether a file was written while the delivery's encode could have written it.</summary>
    private static bool Caused(string path, DateTimeOffset from, DateTimeOffset until)
    {
        DateTime written = File.GetLastWriteTimeUtc(path);

        return written >= from.UtcDateTime && written < until.UtcDateTime;
    }

    /// <summary>Whether a file is already in a recycle area inside the episode's folder.</summary>
    private static bool InRecycleArea(string folder, string path)
    {
        return path[folder.Length..]
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(part => string.Equals(part, RecycleFolder, StringComparison.OrdinalIgnoreCase));
    }
}
