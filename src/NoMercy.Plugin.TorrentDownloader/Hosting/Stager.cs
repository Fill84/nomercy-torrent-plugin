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
    /// <param name="release">
    /// What the plugin decided this release is called. One file takes it as its
    /// name; several are a pack, and keep their own.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<StagedResult>> MoveAsync(
        IReadOnlyList<Staged> staged,
        string from,
        string into,
        string? release,
        CancellationToken ct)
    {
        List<StagedResult> results = [];

        foreach (Staged file in staged)
        {
            // A pack is several episodes under one release name, so naming
            // every file after it would have them overwrite each other. Their
            // own names carry the episode and are what staging matched them by.
            string? named = staged.Count == 1 ? release : null;

            results.Add(await OneAsync(file, from, into, named, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>What the staged file is called.</summary>
    /// <remarks>
    /// The file's own name when nothing better was decided, and never a name
    /// with a character a file system will not take: a release title is text
    /// off a web page, and one colon in it would fail the copy rather than the
    /// naming.
    /// </remarks>
    private static string Named(string source, string? release)
    {
        if (string.IsNullOrWhiteSpace(release))
        {
            return Path.GetFileName(source);
        }

        string clean = string.Concat(release.Where(one => !Path.GetInvalidFileNameChars().Contains(one))).Trim();

        return clean.Length == 0 ? Path.GetFileName(source) : clean + Path.GetExtension(source);
    }

    /// <summary>Takes the download away, if anything will let it.</summary>
    /// <remarks>
    /// A file the torrent client has open cannot be deleted on Windows, and
    /// that is the ordinary case rather than a fault: staging happens the
    /// moment a torrent finishes, and the client is holding every file of it.
    /// </remarks>
    private bool Removed(string source)
    {
        try
        {
            File.Delete(source);

            return true;
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation(
                "{File} was staged and could not be deleted yet: {Reason}",
                Path.GetFileName(source),
                held.Message);

            return false;
        }
    }

    private async Task<StagedResult> OneAsync(
        Staged file,
        string from,
        string into,
        string? release,
        CancellationToken ct)
    {
        string source = Path.Combine(from, file.Path.Replace('/', Path.DirectorySeparatorChar));

        // Flat into the intake folder: the encoder takes a path and has no
        // interest in the folders a torrent came in.
        //
        // Under the release's name rather than the uploader's spelling of it.
        // Everything else already carries what the plugin chose — the grab, the
        // pages, the match staging made — and the file was the one place a
        // site's own tag still travelled: silo.s03e04...[EZTVx.to].mkv. It is
        // also the name the server parses to work out what the file is.
        string destination = Path.Combine(into, Named(source, release));

        try
        {
            Directory.CreateDirectory(into);

            // Shared both ways, because the torrent client is still holding
            // it: it keeps every file of a running torrent open for reading and
            // writing and shares it both ways, since it seeds out of the same
            // handle it downloaded into. A copy asking to share it for reading
            // alone is refused by Windows before a byte is read — the share
            // mode has to allow what the existing handle already has — and that
            // is why nothing had ever reached the owner's library.
            await using (FileStream reading = new(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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

            // The copy is the staging, and it is done. What happens to the
            // download afterwards cannot undo it: the torrent client is still
            // holding that file open and may still be seeding out of it, so
            // deleting it is an attempt and never a condition.
            //
            // It used to be neither. The delete threw, the whole staging was
            // reported as a failure, and the episode sat in the intake folder
            // with its grab marked as though nothing had happened — which is
            // why the owner's library never received one.
            bool held = !Removed(source);

            journal.Finished(
                ActivityStage.Download,
                Path.GetFileName(source),
                held
                    ? $"staged into {into}; the download is still held by the client and goes when the torrent does"
                    : $"staged into {into}");

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
