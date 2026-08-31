using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
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
    /// <param name="show">
    /// The show these episodes are of, so each file can be named after the
    /// episode it holds. Null when the server offered no show for it, and then
    /// each file keeps the name the torrent gave it.
    /// </param>
    /// <param name="resolution">As the release writes it: <c>1080p</c>.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<IReadOnlyList<StagedResult>> MoveAsync(
        IReadOnlyList<Staged> staged,
        string from,
        string into,
        Show? show,
        string? resolution,
        CancellationToken ct)
    {
        List<StagedResult> results = [];

        foreach (Staged file in staged)
        {
            // Each file after its own episode, so a pack needs no special case:
            // its files differ by episode and therefore by name.
            results.Add(await OneAsync(file, from, into, show, resolution, ct).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>What the staged file is called.</summary>
    /// <remarks>
    /// <para>
    /// The show, its year, the episode and the quality. Two releases of one
    /// episode at one quality therefore come to the same name and the same
    /// path, so a second copy of an episode cannot exist — there is nowhere
    /// for it to be.
    /// </para>
    /// <para>
    /// It used to be the release title, which is the uploader's text off a web
    /// page. The owner's intake folder held ten files for five episodes, in
    /// pairs differing only by the site's tag on the end.
    /// </para>
    /// <para>
    /// With no show there is nothing to name it from, and the torrent's own
    /// name is kept rather than a name being made up out of what is to hand.
    /// </para>
    /// </remarks>
    private static string Named(string source, Staged file, Show? show, string? resolution)
    {
        return show is null
            ? Path.GetFileName(source)
            : EpisodeName.For(show.Title, show.Year, file.Episode, resolution, Path.GetExtension(source));
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
        Show? show,
        string? resolution,
        CancellationToken ct)
    {
        string source = Path.Combine(from, file.Path.Replace('/', Path.DirectorySeparatorChar));

        // Flat into the intake folder: the encoder takes a path and has no
        // interest in the folders a torrent came in.
        //
        // Under the episode's own name rather than the release's. It is the
        // name the server parses to work out what the file is, and it is what
        // makes a second copy of one episode impossible.
        string destination = Path.Combine(into, Named(source, file, show, resolution));

        // Copied under a name nothing can mistake for an episode, and given the
        // episode's name only once every byte is there. The destination used to
        // be opened before a byte was read, so a copy that stopped part way — a
        // server shutting down mid-tick is enough — left a truncated file in the
        // intake folder under exactly the name a finished one has. The sweep of
        // that folder matches what it finds against the grabs by release name
        // and dispatches whatever one is waiting on, so the next start handed
        // the encoder half an episode and called it done.
        string part = Path.Combine(into, $".nomercy-{Guid.NewGuid():n}.part");

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
            await using (FileStream writing = new(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await reading.CopyToAsync(writing, ct).ConfigureAwait(false);
            }

            long copied = new FileInfo(part).Length;

            if (copied != file.Length)
            {
                // A copy that ran out of disk half way leaves a file of the
                // wrong length and no exception at all on some file systems.
                File.Delete(part);

                throw new IOException($"copied {copied} bytes of {file.Length}");
            }

            // The one step that makes the episode appear, and it is a rename:
            // there is no moment at which anything can see a partial file under
            // this name.
            File.Move(part, destination, overwrite: true);

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
        finally
        {
            // Including when the tick was cancelled, which is not caught above
            // and must not be — a stopping server is not a staging failure.
            // What it must not do is leave the part behind.
            Discard(part);
        }
    }

    /// <summary>Takes a part-copy away, and never takes the caller down with it.</summary>
    private void Discard(string part)
    {
        try
        {
            File.Delete(part);
        }
        catch (Exception whatever) when (whatever is IOException or UnauthorizedAccessException)
        {
            // A part that will not delete is rubbish in a folder, under a name
            // nothing dispatches. Saying so is the whole of what is owed.
            logger.LogWarning("{Part} could not be removed.", part);
        }
    }
}
