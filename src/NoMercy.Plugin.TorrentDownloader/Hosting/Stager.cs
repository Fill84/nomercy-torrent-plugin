using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>What became of one file on its way to the intake folder.</summary>
/// <param name="File">Which file, and which episode it is for.</param>
/// <param name="Path">Where it now is, when it got there.</param>
/// <param name="Reason">Why it did not, when it did not.</param>
public sealed record StagedResult(Staged File, string? Path, string? Reason)
{
    /// <summary>Whether it is in the intake folder.</summary>
    public bool Moved => Path is not null;
}

/// <summary>
/// Moving a finished download's video into the intake folder.
/// </summary>
/// <remarks>
/// <para>
/// Copy and delete rather than a move, because the incomplete folder and the
/// intake folder are very often on different disks — the download lands on the
/// fast one and the library lives on the big one — and a move across volumes is
/// a copy anyway. Doing it in that order means a failure leaves the download
/// exactly where it was.
/// </para>
/// <para>
/// The download is never touched until the copy is complete and its length
/// matches. Staging is the one point where the plugin writes into the owner's
/// library, and a half-copied episode there is worse than no episode at all.
/// </para>
/// </remarks>
public sealed class Stager(IActivityJournal journal, ILogger logger)
{
    /// <summary>Moves what was chosen, and says what happened to each.</summary>
    /// <param name="staged">What <see cref="Staging.Choose"/> decided.</param>
    /// <param name="from">The download's own folder.</param>
    /// <param name="into">The intake folder.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<StagedResult>> MoveAsync(
        IReadOnlyList<Staged> staged,
        string from,
        string into,
        CancellationToken ct)
    {
        List<StagedResult> results = [];

        foreach (Staged file in staged)
        {
            results.Add(await OneAsync(file, from, into, ct).ConfigureAwait(false));
        }

        return results;
    }

    private async Task<StagedResult> OneAsync(Staged file, string from, string into, CancellationToken ct)
    {
        string source = Path.Combine(from, file.Path.Replace('/', Path.DirectorySeparatorChar));

        // Flat into the intake folder, under the file's own name: the encoder
        // takes a path and has no interest in the folders a torrent came in.
        string destination = Path.Combine(into, Path.GetFileName(source));

        try
        {
            Directory.CreateDirectory(into);

            await using (FileStream reading = new(source, FileMode.Open, FileAccess.Read, FileShare.Read))
            await using (FileStream writing = new(destination, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await reading.CopyToAsync(writing, ct).ConfigureAwait(false);
            }

            long copied = new FileInfo(destination).Length;

            if (copied != file.Length)
            {
                // A copy that ran out of disk half way leaves a file of the
                // wrong length and no exception at all on some file systems.
                File.Delete(destination);

                throw new IOException($"copied {copied} bytes of {file.Length}");
            }

            File.Delete(source);

            journal.Finished(ActivityStage.Download, Path.GetFileName(source), $"staged into {into}");

            return new(file, destination, null);
        }
        catch (Exception refused) when (refused is IOException or UnauthorizedAccessException)
        {
            // Loudly, and the download is left exactly where it was: an
            // unwritable intake folder is something the owner has to fix, and
            // deleting the only copy of the episode while saying so would be
            // unforgivable.
            string reason = $"{Path.GetFileName(source)} could not be staged into {into}: {refused.Message}";

            logger.LogWarning("{Reason}", reason);
            journal.Failed(ActivityStage.Download, Path.GetFileName(source), reason);

            return new(file, null, reason);
        }
    }
}
