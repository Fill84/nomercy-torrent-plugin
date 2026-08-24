using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
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
    ILogger logger,
    TimeProvider? time = null)
{
    /// <summary>
    /// How long an encode is given before it is given up on.
    /// </summary>
    /// <remarks>
    /// The library having the episode is the only proof it finished, and a job
    /// that failed looks exactly like one still running. Six hours is longer
    /// than any episode takes and short enough that an owner is not left
    /// waiting on something that will never arrive.
    /// </remarks>
    public static TimeSpan Patience { get; } = TimeSpan.FromHours(6);

    /// <summary>Since when each dispatched grab has been waited on.</summary>
    /// <remarks>
    /// Held rather than written down: a restart is a good enough reason to
    /// start the clock again, and it saves a column for something the plugin
    /// only needs while it is running.
    /// </remarks>
    private readonly Dictionary<string, DateTimeOffset> _waiting = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One pass over everything the client is holding.</summary>
    /// <param name="incompleteFolder">Where downloads land while they run.</param>
    /// <param name="intakeFolder">Where a finished episode is put for the encoder.</param>
    /// <param name="ct">The plugin's own lifetime, never a caller's request.</param>
    public async Task TickAsync(string incompleteFolder, string intakeFolder, CancellationToken ct)
    {
        IReadOnlyList<TorrentStatus> running = await engine.StatusAsync(ct);
        // One row per torrent. Every cycle used to record a fresh grab for an
        // episode it was already downloading, so one release could have eight
        // rows under one info hash — and every step here walked rows: eight
        // encode jobs for one file, on every tick, which is how the owner's
        // History page filled with the same episode dispatched over and over.
        IReadOnlyList<StoredDownload> stored =
        [
            .. (await grabs.OpenAsync(ct))
                .GroupBy(one => one.InfoHash, StringComparer.OrdinalIgnoreCase)
                .Select(same => same.First()),
        ];

        // Failures first, and out of the way: a torrent the client has given up
        // on is neither something to carry nor something to stage, and leaving
        // it in either pile would have recovery re-add it every minute.
        IReadOnlyList<string> failed = await FailedAsync(running, stored, ct);

        // Before the plan, and counted with the failures: a grab cancelled here
        // that was still in the plan would be carried on the same tick and put
        // straight back to downloading.
        failed = [.. failed, .. await NotOursAsync(stored, ct)];

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
        await LeftBehindAsync(intakeFolder, ct);
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

            // The show and the quality are what the episode is named after, and
            // both are known here: the show from the library, the quality from
            // the release the plugin chose. A show the server does not offer
            // leaves the file under the torrent's own name rather than under a
            // name made up from what is to hand.
            Show? show = (await library.GetShowsAsync(ct))
                .FirstOrDefault(candidate => candidate.Id == finished.Covers.FirstOrDefault().ShowId);

            string? resolution = ReleaseName.Parse(finished.ReleaseTitle).Resolution;

            IReadOnlyList<StagedResult> moved =
                await stager.MoveAsync(chosen, incompleteFolder, intakeFolder, show, resolution, ct);

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

        // Where this show's episodes already are, so the encode goes to the
        // folder it really lives in. A library can have several.
        string? existing = (await library.GetShowFilesAsync(episode.ShowId, ct)).FirstOrDefault();

        bool queued = await dispatch.DispatchAsync(
            staged,
            show.LibraryId,
            show.Kind == LibraryKind.Anime ? "anime" : "tv",
            existing,
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

        // The clock starts here, not on the tick that next looks at it: an
        // encode is waited on from the moment it was asked for.
        _waiting[infoHash] = (time ?? TimeProvider.System).GetUtcNow();
    }

    /// <summary>
    /// Cancels a grab for a show the owner does not have, and deletes what it
    /// downloaded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On 24 August 2026 the library rule was widened to every show in a
    /// library, and within the hour the plugin was on 479 grabs: the server
    /// keeps rows for shows nobody asked for, and Family Guy alone claimed 456
    /// missing episodes.
    /// </para>
    /// <para>
    /// Putting the rule back stops more being made and does nothing about the
    /// ones already running. Leaving those to finish would fill the owner's
    /// disk with shows they have never watched, so they go, and take their
    /// bytes with them.
    /// </para>
    /// <para>
    /// One episode on disk is what says a show is theirs, which is the same
    /// rule the refresh uses. A grab that has already staged its episode is
    /// left alone: it is past this point and its show now has a file either
    /// way.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> NotOursAsync(IReadOnlyList<StoredDownload> stored, CancellationToken ct)
    {
        Dictionary<int, bool> owned = [];
        List<string> cancelled = [];

        foreach (StoredDownload open in stored)
        {
            if (open.State is GrabState.Staged or GrabState.Dispatched)
            {
                continue;
            }

            bool theirs = true;

            foreach (int show in open.Covers.Select(one => one.ShowId).Distinct())
            {
                if (!owned.TryGetValue(show, out bool has))
                {
                    has = (await library.GetEpisodesAsync(show, ct)).Any(one => one.HasFile);
                    owned[show] = has;
                }

                theirs &= has;
            }

            if (theirs)
            {
                continue;
            }

            string reason = "it is not a show the owner has, so it was cancelled and its download deleted";

            await engine.RemoveAsync(open.InfoHash, deleteFiles: true, ct);
            await grabs.FailedAsync(open.InfoHash, reason, DateTimeOffset.UtcNow, ct);

            logger.LogWarning("{Release}: {Reason}", open.ReleaseTitle, reason);
            journal.Failed(ActivityStage.Download, open.ReleaseTitle, reason);

            cancelled.Add(open.InfoHash);
        }

        return cancelled;
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
            if (staged.StagedPath is not string path)
            {
                // Staged by a version that did not record where. The folder is
                // read directly for those, which is LeftBehindAsync.
                continue;
            }

            if (!File.Exists(path))
            {
                // Gone. The encode was never taken and there is nothing left to
                // offer, so there is nothing to wait for — and waiting is what
                // this used to do, silently and for ever, with the episode
                // neither in the library nor being looked for.
                //
                // Whether the owner moved it or something deleted it cannot be
                // told from here, and either way the library does not have it.
                string reason = $"{Path.GetFileName(path)} was staged and is no longer there";

                await grabs.FailedAsync(staged.InfoHash, reason, DateTimeOffset.UtcNow, ct);

                logger.LogWarning("{Release}: {Reason}", staged.ReleaseTitle, reason);
                journal.Failed(ActivityStage.Dispatch, staged.ReleaseTitle, reason);

                continue;
            }

            foreach (EpisodeKey episode in staged.Covers)
            {
                await DispatchAsync(staged.InfoHash, episode, path, ct);
            }
        }
    }

    /// <summary>
    /// Asks for an encode for anything in the intake folder nothing is waiting
    /// on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Before a grab recorded where it staged its episode, it was marked done
    /// the moment the file was copied — whether or not the encode had been
    /// taken. Three of the owner's episodes were left in the intake folder that
    /// way, with nothing in the plugin that would ever come back to them.
    /// </para>
    /// <para>
    /// So the folder itself is read. Anything no open grab is waiting on is
    /// matched to the grab that put it there, by the release both carry, and
    /// asked for again — after which it is on the ordinary path: dispatched,
    /// then the library, then deleted.
    /// </para>
    /// <para>
    /// A file no grab can be found for is left alone and said once. It may be
    /// something the owner put there by hand, and this plugin does not delete
    /// what it did not make.
    /// </para>
    /// </remarks>
    private async Task LeftBehindAsync(string intakeFolder, CancellationToken ct)
    {
        if (!Directory.Exists(intakeFolder))
        {
            return;
        }

        // Read again rather than taken from the top of the tick. Staging has
        // happened since, and a file staged a moment ago would be read as one
        // nothing is waiting on — so it was dispatched a second time, on every
        // tick, for every episode that had just been staged.
        HashSet<string> waited = new(
            (await grabs.OpenAsync(ct)).Select(one => one.StagedPath).OfType<string>(),
            StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<StoredDownload>? every = null;

        foreach (string entry in Directory.EnumerateFileSystemEntries(intakeFolder))
        {
            // A folder is never something this plugin put here: staging copies
            // the video out flat, because the encoder takes a path and has no
            // interest in the folders a torrent came in. The owner's intake
            // folder held six left by 0.3.4, still carrying the tracker's name.
            if (Directory.Exists(entry))
            {
                Discard(entry, "a folder no download of this plugin's uses");

                continue;
            }

            if (waited.Contains(entry))
            {
                continue;
            }

            every ??= await grabs.EveryAsync(ct);

            // By the release, so the uploader's spelling of it and the name the
            // plugin chose come to the same thing.
            string named = TitleMatcher.Release(Path.GetFileNameWithoutExtension(entry));

            StoredDownload? put = every.FirstOrDefault(one =>
                string.Equals(TitleMatcher.Release(one.ReleaseTitle), named, StringComparison.Ordinal));

            // Nothing needs it. Cleared rather than left, which is what the
            // owner asked for on 24 August 2026: the plugin used to leave
            // whatever it could not account for, and the folder only ever grew
            // — twenty-two things for five episodes, read again on every tick.
            //
            // A grab that already knows where its file is is being waited on,
            // so another file matching it is a second copy. That cannot happen
            // any more, since an episode's name comes from the episode, but the
            // ones already on the owner's disk are still there.
            if (put is null)
            {
                Discard(entry, "no grab of this plugin's is waiting on it");

                continue;
            }

            if (put.StagedPath is not null)
            {
                Discard(entry, $"a second copy of {put.ReleaseTitle}");

                continue;
            }

            await grabs.StagedAsync(put.InfoHash, entry, ct);

            foreach (EpisodeKey episode in put.Covers)
            {
                await DispatchAsync(put.InfoHash, episode, entry, ct);
            }
        }
    }

    /// <summary>Takes something out of the intake folder and says why.</summary>
    /// <remarks>
    /// Said every time rather than once, because this deletes the owner's files
    /// and a deletion nobody can account for afterwards is worse than the
    /// clutter it cleared. Nothing throws: a file the encoder has open comes
    /// round again on the next tick.
    /// </remarks>
    private void Discard(string entry, string why)
    {
        try
        {
            if (Directory.Exists(entry))
            {
                Directory.Delete(entry, recursive: true);
            }
            else
            {
                File.Delete(entry);
            }

            logger.LogInformation(
                "{Entry} was cleared from the intake folder: {Why}.",
                Path.GetFileName(entry),
                why);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            logger.LogInformation(
                "{Entry} could not be cleared from the intake folder and was left: {Reason}",
                Path.GetFileName(entry),
                held.Message);
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
                await StillWaitingAsync(sent, ct);

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

    /// <summary>
    /// Gives up on an encode the library never received.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not dispatched a second time. A second job for a file the encoder may
    /// still be working on is the one thing worse than waiting, and nothing
    /// here can tell a job that failed from one still running — the plugin
    /// cannot see the queue.
    /// </para>
    /// <para>
    /// The episode goes back to missing so it can be found again, and the
    /// staged file is left where it is: if the encode does land later, the
    /// refresh sees the file and the episode stops being missing on its own.
    /// </para>
    /// </remarks>
    private async Task StillWaitingAsync(StoredDownload sent, CancellationToken ct)
    {
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();

        if (!_waiting.TryGetValue(sent.InfoHash, out DateTimeOffset since))
        {
            // Dispatched by a previous run of the plugin. A restart is a good
            // enough reason to start the clock again rather than give up on
            // something that may be minutes from finishing.
            _waiting[sent.InfoHash] = now;

            return;
        }

        if (now - since < Patience)
        {
            return;
        }

        _waiting.Remove(sent.InfoHash);

        string reason =
            $"the encode was asked for {Patience.TotalHours:0} hours ago and the library still does not have it";

        await grabs.FailedAsync(sent.InfoHash, reason, now, ct);

        logger.LogWarning("{Release}: {Reason}", sent.ReleaseTitle, reason);
        journal.Failed(ActivityStage.Dispatch, sent.ReleaseTitle, reason);
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
