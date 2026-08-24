using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Storage;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The tick that turns a finished download into an episode.
/// </summary>
/// <remarks>
/// <para>
/// Sprint 6 built the grab, the staging and the encode dispatch, and no slice
/// ever called any of them: a download that finished sat in the incomplete
/// folder for ever while its episode showed as unavailable. This is the loop
/// that joins them, and it runs on the fastest cadence because a completion
/// nobody notices is an episode nobody gets.
/// </para>
/// <para>
/// Nothing here throws. It once unwound the whole cadence, so one type mismatch
/// in the encoder stopped every download in flight from being looked at — every
/// torrent is dealt with on its own and a failure costs that one.
/// </para>
/// </remarks>
public sealed class Transfers(
    ITorrentEngine engine,
    GrabRepository grabs,
    ILibrary library,
    Stager stager,
    EncodeDispatch dispatch,
    IActivityJournal journal,
    ILogger logger)
{
    /// <summary>One pass over everything the client is holding.</summary>
    /// <param name="incompleteFolder">Where downloads land while they run.</param>
    /// <param name="intakeFolder">Where a finished episode is put for the encoder.</param>
    /// <param name="ct">The plugin's own lifetime, never a caller's request.</param>
    public async Task TickAsync(string incompleteFolder, string intakeFolder, CancellationToken ct)
    {
        IReadOnlyList<TorrentStatus> running = await engine.StatusAsync(ct);
        IReadOnlyList<StoredDownload> stored = await grabs.OpenAsync(ct);

        // Failures first, and out of the way: a torrent the client has given up
        // on is neither something to carry nor something to stage, and leaving
        // it in either pile would have recovery re-add it every minute.
        IReadOnlyList<string> failed = await FailedAsync(running, stored, ct);

        RecoveryPlan plan = Recovery.Plan(
            [.. stored.Where(one => !failed.Contains(one.InfoHash, StringComparer.OrdinalIgnoreCase))],
            [.. running.Where(one => !failed.Contains(one.InfoHash, StringComparer.OrdinalIgnoreCase))]);

        foreach (StoredDownload lost in plan.Add)
        {
            await AddAgainAsync(lost, incompleteFolder, ct);
        }

        foreach (TorrentStatus unknown in plan.Stop)
        {
            // Kept, never deleted. Something this plugin has no record of may be
            // half a film the owner has been waiting for, and a record can be
            // lost by a restore of an older database.
            await engine.RemoveAsync(unknown.InfoHash, deleteFiles: false, ct);

            logger.LogInformation(
                "{Hash} is in the client and not in the store, so it was stopped and its files left alone.",
                unknown.InfoHash);
        }

        foreach (StoredDownload finished in plan.Stage)
        {
            await StageAsync(finished, incompleteFolder, intakeFolder, ct);
        }

        await AskAgainAsync(stored, ct);
        await FinishAsync(stored, running, ct);

        foreach (StoredDownload carrying in plan.Carry)
        {
            if (carrying.State != GrabState.Downloading)
            {
                await grabs.StateAsync(carrying.InfoHash, GrabState.Downloading, ct);
            }
        }
    }

    /// <summary>
    /// Every torrent the client has given up on, blacklisted and put back.
    /// </summary>
    /// <remarks>
    /// Both halves and one transaction, which is the store's business. Without
    /// the blacklist the next cycle chooses the same release and fails the same
    /// way for as long as the plugin runs; without the return the episodes look
    /// grabbed for ever.
    /// </remarks>
    private async Task<IReadOnlyList<string>> FailedAsync(
        IReadOnlyList<TorrentStatus> running,
        IReadOnlyList<StoredDownload> stored,
        CancellationToken ct)
    {
        List<string> failed = [];

        foreach (TorrentStatus status in running.Where(one => one.State == TorrentState.Error))
        {
            if (!stored.Any(one => string.Equals(one.InfoHash, status.InfoHash, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            // The client's own words. "Failed" without one is the entry the
            // owner opens the History page to understand and learns nothing
            // from.
            string reason = status.Error ?? "the torrent client gave up on it and said no more than that";

            await grabs.FailedAsync(status.InfoHash, reason, DateTimeOffset.UtcNow, ct);
            await engine.RemoveAsync(status.InfoHash, deleteFiles: false, ct);

            journal.Failed(ActivityStage.Download, status.Name ?? status.InfoHash, reason);
            failed.Add(status.InfoHash);
        }

        return failed;
    }

    /// <summary>Hands a torrent the client has lost back to it.</summary>
    /// <remarks>
    /// From the magnet the store kept rather than by searching again: the bytes
    /// are still on disk with the resume file beside them, so this costs a
    /// verification pass and not a download.
    /// </remarks>
    private async Task AddAgainAsync(StoredDownload lost, string incompleteFolder, CancellationToken ct)
    {
        if (lost.Magnet.Length == 0)
        {
            // Nothing to re-add it from. Said rather than skipped in silence, or
            // the row sits on the Downloads page for ever saying the client has
            // not taken it up.
            logger.LogWarning(
                "{Release} was grabbed and the client has lost it, and no magnet was kept to add it again.",
                lost.ReleaseTitle);

            return;
        }

        try
        {
            await engine.AddAsync(new(lost.Magnet, [], incompleteFolder, null), ct);
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            // One torrent is one torrent. A client that would not take this one
            // must not stop the others from being looked at.
            logger.LogWarning("{Release} could not be added again: {Reason}", lost.ReleaseTitle, refused.Message);
        }
    }

    /// <summary>
    /// Moves what finished into the intake folder and asks for its encode.
    /// </summary>
    /// <remarks>
    /// An episode no file answered for leaves the grab where it is rather than
    /// being marked done: a pack missing an episode is worth saying so about,
    /// and the episode is looked for again.
    /// </remarks>
    private async Task StageAsync(
        StoredDownload finished,
        string incompleteFolder,
        string intakeFolder,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TorrentFile> files = await engine.FilesAsync(finished.InfoHash, ct);
            IReadOnlyList<Staged> chosen = Staging.Choose(files, finished.Covers);

            foreach (EpisodeKey unanswered in Staging.Unanswered(chosen, finished.Covers))
            {
                journal.Failed(
                    ActivityStage.Download,
                    $"{finished.ReleaseTitle} {unanswered}",
                    "no file in the torrent answers for it, so it is still missing");
            }

            IReadOnlyList<StagedResult> moved = await stager.MoveAsync(chosen, incompleteFolder, intakeFolder, ct);

            if (!moved.Any(one => one.Moved))
            {
                // Nothing reached the intake folder, so nothing is done with.
                // Marking it done would lose the download and the episode.
                return;
            }

            // Staged, and said so before the encode is asked for. The copy has
            // happened and must not happen again; whether the encode is taken
            // is a separate question with its own answer, and a grab that
            // claimed to be done the moment the file was copied forgot every
            // encode that was refused.
            StagedResult first = moved.First(one => one.Moved);

            await grabs.StagedAsync(finished.InfoHash, first.Path!, ct);

            foreach (StagedResult one in moved.Where(one => one.Moved))
            {
                await DispatchAsync(finished.InfoHash, one.File.Episode, one.Path!, ct);
            }
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            logger.LogWarning("{Release} could not be staged: {Reason}", finished.ReleaseTitle, wrong.Message);
            journal.Failed(ActivityStage.Download, finished.ReleaseTitle, wrong.Message);
        }
    }

    /// <summary>Asks the server to encode one staged file into the show's own library.</summary>
    /// <remarks>
    /// The show's own library, so an anime episode is dispatched to the anime
    /// library and a television one to the tv library. This plugin never picks
    /// a library: it reads the one the show is already in.
    /// </remarks>
    private async Task DispatchAsync(string infoHash, EpisodeKey episode, string staged, CancellationToken ct)
    {
        Show? show = (await library.GetShowsAsync(ct))
            .FirstOrDefault(candidate => candidate.Id == episode.ShowId);

        if (show is null)
        {
            logger.LogWarning(
                "{File} was staged and show {Show} is in no library the server offered, so no encode was asked for.",
                staged,
                episode.ShowId);

            return;
        }

        bool queued = await dispatch.DispatchAsync(
            staged,
            show.LibraryId,
            show.Kind == LibraryKind.Anime ? "anime" : "tv",
            ct);

        if (!queued)
        {
            // Left staged, so the next tick asks again without copying the file
            // a second time. An encode refused because the server could not yet
            // identify the file is refused for a reason that can change.
            return;
        }

        await grabs.DispatchedAsync(
            episode,
            show.Title,
            Path.GetFileName(staged),
            show.LibraryId,
            DateTimeOffset.UtcNow,
            ct);

        await grabs.StateAsync(infoHash, GrabState.Dispatched, ct);
    }

    /// <summary>
    /// Asks again for every encode that was staged and never taken.
    /// </summary>
    /// <remarks>
    /// The file is already in the intake folder, so nothing is copied. An
    /// encode is refused for reasons that change — a server still starting up,
    /// a show it has not finished importing — and a grab that gave up on the
    /// first refusal left the episode in a folder nobody is watching, which is
    /// where three of the owner's were found.
    /// </remarks>
    private async Task AskAgainAsync(IReadOnlyList<StoredDownload> stored, CancellationToken ct)
    {
        foreach (StoredDownload staged in stored.Where(one => one.State == GrabState.Staged))
        {
            if (staged.StagedPath is not string path || !File.Exists(path))
            {
                // Staged by a version that did not record where, or the owner
                // moved it. Nothing to ask about and nothing to delete.
                continue;
            }

            foreach (EpisodeKey episode in staged.Covers)
            {
                await DispatchAsync(staged.InfoHash, episode, path, ct);
            }
        }
    }

    /// <summary>
    /// Clears up after an encode that has landed in the library.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The library having the episode is the only proof that the encode
    /// finished — the plugin cannot see the server's queue, and the file
    /// appearing is what it was all for.
    /// </para>
    /// <para>
    /// Then the copy in the intake folder goes, and so does the torrent and
    /// what it downloaded. Left behind they are two more copies of an episode
    /// the owner already has, re-checked on every start for ever.
    /// </para>
    /// </remarks>
    private async Task FinishAsync(
        IReadOnlyList<StoredDownload> stored,
        IReadOnlyList<TorrentStatus> running,
        CancellationToken ct)
    {
        foreach (StoredDownload sent in stored.Where(one => one.State == GrabState.Dispatched))
        {
            if (running.Any(one =>
                    string.Equals(one.InfoHash, sent.InfoHash, StringComparison.OrdinalIgnoreCase)
                    && one.State == TorrentState.Seeding))
            {
                // Still giving something back. The library having the episode
                // says the encode finished; it says nothing about what the
                // torrent still owes — a private one seeds to the owner's ratio
                // or hours, and the library can have the episode long before
                // either. Deleting then costs the owner the account the seeding
                // rules exist to protect.
                //
                // Nothing is lost by waiting: the episode is already in the
                // library. The tick after the seed limit stops it finishes this.
                continue;
            }

            bool landed = true;

            foreach (IGrouping<int, EpisodeKey> show in sent.Covers.GroupBy(one => one.ShowId))
            {
                IReadOnlyList<Episode> episodes = await library.GetEpisodesAsync(show.Key, ct);

                landed &= show.All(wanted => episodes.Any(one =>
                    one.Season == wanted.Season && one.Number == wanted.Number && one.HasFile));
            }

            if (!landed)
            {
                continue;
            }

            if (sent.StagedPath is string path)
            {
                Delete(path);
            }

            // The torrent and its download with it, now that it is not seeding
            // any more.
            await engine.RemoveAsync(sent.InfoHash, deleteFiles: true, ct);
            await grabs.StateAsync(sent.InfoHash, GrabState.Done, ct);

            journal.Finished(ActivityStage.Dispatch, sent.ReleaseTitle, "encoded into the library, and the copies deleted");
        }
    }

    /// <summary>Takes a file away, and never takes the caller down with it.</summary>
    private void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Something still has it open. It is one file left behind, which is
            // not worth stopping the tick for; the next one tries again.
            logger.LogInformation("{File} could not be deleted: {Reason}", Path.GetFileName(path), held.Message);
        }
    }
}
