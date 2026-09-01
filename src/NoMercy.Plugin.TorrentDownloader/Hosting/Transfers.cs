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
    IEncodeGateway dispatch,
    IActivityJournal journal,
    ILogger logger,
    TimeProvider? time = null,
    IEncodeJobs? jobs = null,
    IShowImport? imports = null)
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
        // Everything the library is asked in this pass, asked once. It lives
        // for this tick and no longer: the server encodes while the plugin
        // runs, and an answer kept past the tick that asked for it would be a
        // decision made on what used to be true.
        LibraryThisTick thisTick = new(library);

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
        failed = [.. failed, .. await NotOursAsync(stored, thisTick, ct)];

        // What is still open after this tick's failures, which is what the
        // store would answer if it were asked again.
        IReadOnlyList<StoredDownload> open =
            [.. stored.Where(one => !failed.Contains(one.InfoHash, StringComparer.OrdinalIgnoreCase))];

        RecoveryPlan plan = Recovery.Plan(
            open,
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

        // Staging says what it wrote, because the next step needs to know and
        // used to read the open grabs a second time to find out.
        //
        // Every file, not the first. A pack stages an episode per file and
        // answered with one of them, so the sweep below saw eight files nothing
        // was waiting on and deleted them a second after their encodes had been
        // asked for — nine dispatched at 12:22:40 on 1 September 2026, eight
        // gone by 12:22:46, one episode in the library at the end of it.
        List<string> justStaged = [];

        foreach (StoredDownload finished in plan.Stage)
        {
            justStaged.AddRange(await StageAsync(finished, incompleteFolder, intakeFolder, thisTick, ct));
        }

        // Every staged file something is waiting on: what the store knew at the
        // top of the tick, less what failed during it, plus what staging has
        // written since. Without the last part a file staged a moment ago reads
        // as one nothing is waiting on, and it was dispatched a second time on
        // every tick for every episode that had just been staged.
        //
        // A grab that AskAgainAsync fails below is still counted here, and that
        // costs nothing: it fails precisely because its staged file is no longer
        // there, so no entry in the folder can match it.
        HashSet<string> waited = new(
            open.SelectMany(one => one.StagedPaths).Concat(justStaged),
            StringComparer.OrdinalIgnoreCase);

        await AskAgainAsync(stored, thisTick, ct);
        await LeftBehindAsync(intakeFolder, waited, thisTick, ct);
        await FinishAsync(stored, running, thisTick, ct);

        foreach (StoredDownload carrying in plan.Carry)
        {
            if (carrying.State != GrabState.Downloading)
            {
                await grabs.StateAsync(carrying.InfoHash, GrabState.Downloading, ct);
            }
        }
    }

    /// <summary>How long a refusal about one moment stands before it runs out.</summary>
    /// <remarks>
    /// <para>
    /// A swarm that did not answer tonight may be there tomorrow, and until
    /// this existed every refusal was for ever: South Park S15E12 1080p HMAX
    /// CtrlHD was blacklisted on 25 August 2026 because no peer sent its
    /// metadata within five minutes, and on 31 August it sat on TorrentBay with
    /// fifty seeders while the plugin would not look at it and the owner
    /// watched it settle for a 720p.
    /// </para>
    /// <para>
    /// Six hours, so a release is tried four times a day rather than once and
    /// never. Each retry costs one metadata wait, which is minutes; refusing
    /// for ever costs the episode.
    /// </para>
    /// </remarks>
    public static readonly TimeSpan RefusedFor = TimeSpan.FromHours(6);

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

            DateTimeOffset when = DateTimeOffset.UtcNow;

            await grabs.FailedAsync(
                status.InfoHash,
                reason,

                // For ever only where the torrent's own contents are the
                // reason. Everything else the client gives up on is about
                // tonight, and comes round again.
                when,
                status.ErrorIsTheRelease ? null : when + RefusedFor,
                ct);

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
    /// <returns>
    /// Every file it put in the intake folder, which is empty when it put
    /// none. The tick needs all of them: a file staged a moment ago is one
    /// something is waiting on, and the sweep deletes what nothing is waiting
    /// on.
    /// </returns>
    private async Task<IReadOnlyList<string>> StageAsync(
        StoredDownload finished,
        string incompleteFolder,
        string intakeFolder,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        try
        {
            IReadOnlyList<TorrentFile> files = await engine.FilesAsync(finished.InfoHash, ct);

            // A torrent added by hand covers no episode, deliberately: claiming
            // one nobody chose would put that episode back to missing if the
            // download failed. docs/08-ui.md § Actions says it is staged and
            // dispatched like any other, so this is where it finds out what it
            // holds — from its own file names, once there are files to read.
            // Written down as well as used, because every step after staging
            // reads the episodes back out of the store.
            IReadOnlyList<EpisodeKey> covers = finished.Covers;

            if (covers.Count == 0)
            {
                covers = Staging.Discover(files, await thisTick.GetShowsAsync(ct));

                if (covers.Count == 0)
                {
                    // Nothing the owner has. The show is looked up and added,
                    // which is the one thing that turns this into an ordinary
                    // grab: once it is in a library it has episodes, and an
                    // episode has the id an encode is asked for by.
                    //
                    // It is the same call the dashboard's Add content makes —
                    // DispatchJob<ShowImportJob>(id, libraryId) — and it is the
                    // only thing that adds a show. Handing the files to the
                    // encoder without an id does not: PluginEncoder writes the
                    // media id straight into VideoEncodeJob.Id and that job
                    // resolves it against Movies.Id or Episodes.Id and nothing
                    // else, so no id resolves no row, the job returns having
                    // done no work, and the queue records it finished. On 31
                    // August 2026 that was nine files, nine jobs finished inside
                    // two minutes, and nothing written to the library.
                    //
                    // Nothing else happens this tick. The import runs on the
                    // server's own queue, and the tick after it lands sees this
                    // like any other grab — matched by name, covered, staged,
                    // dispatched by each episode's own id.
                    if (await AddedAsync(files, thisTick, ct))
                    {
                        return [];
                    }

                    // Only where that could not be done: no library of the kind
                    // its files read as, no provider that knows the show, or a
                    // server without the parts. Then it is named, said out loud,
                    // and left exactly where the owner put it.
                    await UnplaceableAsync(finished, files, ct);

                    return [];
                }

                // No longer unplaceable, if it ever was: the show has been
                // added since, and a later one must be said out loud again.
                _unplaceable.Remove(finished.InfoHash);

                await grabs.CoversAsync(finished.InfoHash, covers, ct);

                journal.Finished(
                    ActivityStage.Download,
                    finished.ReleaseTitle,
                    $"added by hand, and it holds {covers.Count} episode{(covers.Count == 1 ? string.Empty : "s")}");
            }

            IReadOnlyList<Staged> chosen = Staging.Choose(files, covers);

            foreach (EpisodeKey unanswered in Staging.Unanswered(chosen, covers))
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
            Show? show = (await thisTick.GetShowsAsync(ct))
                .FirstOrDefault(candidate => candidate.Id == covers.FirstOrDefault().ShowId);

            string? resolution = ReleaseName.Parse(finished.ReleaseTitle).Resolution;

            IReadOnlyList<StagedResult> moved =
                await stager.MoveAsync(chosen, incompleteFolder, intakeFolder, show, resolution, ct);

            if (!moved.Any(one => one.Moved))
            {
                // Nothing reached the intake folder, so nothing is done with.
                // Marking it done would lose the download and the episode.
                return [];
            }

            // Staged, and said so before the encode is asked for. The copy has
            // happened and must not happen again; whether the encode is taken
            // is a separate question with its own answer, and a grab that
            // claimed to be done the moment the file was copied forgot every
            // encode that was refused.
            string[] staged = [.. moved.Where(one => one.Moved).Select(one => one.Path!)];

            await grabs.StagedAsync(finished.InfoHash, staged, ct);

            foreach (StagedResult one in moved.Where(one => one.Moved))
            {
                await DispatchAsync(finished.InfoHash, one.File.Episode, one.Path!, thisTick, ct);
            }

            return staged;
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            logger.LogWarning("{Release} could not be staged: {Reason}", finished.ReleaseTitle, wrong.Message);
            journal.Failed(ActivityStage.Download, finished.ReleaseTitle, wrong.Message);

            return [];
        }
    }

    /// <summary>Asks the server to encode one staged file into the show's own library.</summary>
    /// <remarks>
    /// The show's own library, so an anime episode is dispatched to the anime
    /// library and a television one to the tv library. This plugin never picks
    /// a library: it reads the one the show is already in.
    /// </remarks>
    private async Task DispatchAsync(
        string infoHash,
        EpisodeKey episode,
        string staged,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        Show? show = (await thisTick.GetShowsAsync(ct))
            .FirstOrDefault(candidate => candidate.Id == episode.ShowId);

        if (show is null)
        {
            logger.LogWarning(
                "{File} was staged and show {Show} is in no library the server offered, so no encode was asked for.",
                staged,
                episode.ShowId);

            return;
        }

        // The row the tick already has, rather than one the gateway fetches for
        // itself. Asked in there it was one question per episode, so a season
        // pack asked the server the same one nine times — against the rule this
        // whole tick is built on.
        Episode? row = (await thisTick.GetEpisodesAsync(episode.ShowId, ct))
            .FirstOrDefault(one => one.Season == episode.Season && one.Number == episode.Number);

        if (row is null)
        {
            logger.LogWarning(
                "{File} was staged and the server lists no {Episode} for {Show}, so no encode was asked for.",
                staged,
                episode,
                show.Title);

            return;
        }

        EncodeAsk asked = await dispatch.DispatchAsync(staged, row, show, ct);

        if (!asked.Taken)
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
            show.LibraryName,
            DateTimeOffset.UtcNow,
            ct);

        await grabs.StateAsync(infoHash, GrabState.Dispatched, ct);

        // The job it queued, where the server named one, so a restart does not
        // lose which encode this grab is waiting on. media-server #31.
        if (asked.JobId is string job)
        {
            await grabs.EncodeJobAsync(infoHash, episode, job, ct);
        }

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
    /// Whose show it is comes from <c>Ownership.Theirs</c>, which is where the
    /// rule and its reasoning live and is the same call the refresh makes. A
    /// grab that has already staged its episode is left alone: it is past this
    /// point and its show now has a file either way.
    /// </para>
    /// </remarks>
    private async Task<IReadOnlyList<string>> NotOursAsync(
        IReadOnlyList<StoredDownload> stored,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        List<string> cancelled = [];

        foreach (StoredDownload open in stored)
        {
            if (open.State is GrabState.Staged or GrabState.Dispatched)
            {
                continue;
            }

            bool theirs = true;

            // No cache of its own. This kept one dictionary of which shows have
            // a file while FinishAsync fetched the same episodes again for its
            // own purposes: one tick, one question, two answers. The tick's
            // library remembers it for both.
            foreach (int show in open.Covers.Select(one => one.ShowId).Distinct())
            {
                theirs &= Ownership.Theirs(await thisTick.GetEpisodesAsync(show, ct));
            }

            if (theirs)
            {
                continue;
            }

            string reason = "it is not a show the owner has, so it was cancelled and its download deleted";

            await engine.RemoveAsync(open.InfoHash, deleteFiles: true, ct);

            // Not about the release at all — about which shows are in the
            // owner's libraries, which is a thing that changes.
            await grabs.FailedAsync(
                open.InfoHash,
                reason,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow + RefusedFor,
                ct);

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
    private async Task AskAgainAsync(
        IReadOnlyList<StoredDownload> stored,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        foreach (StoredDownload staged in stored.Where(one => one.State == GrabState.Staged))
        {
            if (staged.StagedPaths.Count == 0)
            {
                // Staged by a version that did not record where. The folder is
                // read directly for those, which is LeftBehindAsync.
                continue;
            }

            if (staged.StagedPaths.FirstOrDefault(one => !File.Exists(one)) is string missing)
            {
                // Gone. The encode was never taken and there is nothing left to
                // offer, so there is nothing to wait for — and waiting is what
                // this used to do, silently and for ever, with the episode
                // neither in the library nor being looked for.
                //
                // Whether the owner moved it or something deleted it cannot be
                // told from here, and either way the library does not have it.
                string reason = $"{Path.GetFileName(missing)} was staged and is no longer there";

                await grabs.FailedAsync(
                    staged.InfoHash,
                    reason,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow + RefusedFor,
                    ct);

                logger.LogWarning("{Release}: {Reason}", staged.ReleaseTitle, reason);
                journal.Failed(ActivityStage.Dispatch, staged.ReleaseTitle, reason);

                continue;
            }

            // Each episode against the file staged for it, never against the
            // first of them: a pack asked for nine encodes over one path put
            // nine episodes in the library from the same video.
            foreach (EpisodeKey episode in staged.Covers)
            {
                if (Staged(staged, episode) is not string path)
                {
                    continue;
                }

                await DispatchAsync(staged.InfoHash, episode, path, thisTick, ct);
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
    /// <para>
    /// <c>waited</c> is every staged file something is already waiting on,
    /// worked out by the tick. It used to be a second read of the open grabs,
    /// made here because staging has happened since the first one — a file
    /// staged a moment ago would otherwise read as one nothing is waiting on,
    /// and be dispatched a second time on every tick.
    /// </para>
    /// </remarks>
    private async Task LeftBehindAsync(
        string intakeFolder,
        HashSet<string> waited,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        if (!Directory.Exists(intakeFolder))
        {
            return;
        }

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

            // Whatever became of its grab, a file an encode is still reading
            // stays. This step decides on "is a grab waiting on this", and a
            // grab that has just failed or finished is waiting on nothing — so
            // on 1 September 2026 it deleted nine staged files a minute after
            // one episode's encode died, and took episode five's input away
            // between its first bundle and its second. The encoder opens its
            // input once per bundle, and the server saying the job is still
            // going is the only thing here that can know that.
            StoredDownload? reading = every.FirstOrDefault(one =>
                one.StagedPaths.Contains(entry, StringComparer.OrdinalIgnoreCase));

            if (reading is not null
                && await StandingAsync(reading, ct) is { State: EncodeJobState.Queued or EncodeJobState.Running })
            {
                continue;
            }

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

            if (put.StagedPaths.Count > 0)
            {
                Discard(entry, $"a second copy of {put.ReleaseTitle}");

                continue;
            }

            await grabs.StagedAsync(put.InfoHash, [entry], ct);

            foreach (EpisodeKey episode in put.Covers)
            {
                await DispatchAsync(put.InfoHash, episode, entry, thisTick, ct);
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
        LibraryThisTick thisTick,
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

            if (sent.Covers.Count == 0)
            {
                // Handed over for the server to identify, and there is nothing
                // here that can tell whether it arrived. The rule below reads
                // "every episode it covers has a file", which over no episodes
                // is true of nothing — that vacuous truth deleted a 36 GB pack
                // two minutes after handing it over.
                //
                // The job was tried next and is no better. On 31 August 2026
                // nine jobs came back finished inside two minutes, the library
                // gained not one file, and this method deleted the same 36 GB
                // again on their word. A server saying a job is over is not the
                // episode being there, and for a handover nothing else is left
                // to ask.
                //
                // So it is never deleted here. A pack this plugin cannot verify
                // is one it must not throw away, and the owner decides what
                // becomes of it.
                continue;
            }

            bool landed = true;

            foreach (IGrouping<int, EpisodeKey> show in sent.Covers.GroupBy(one => one.ShowId))
            {
                IReadOnlyList<Episode> episodes = await thisTick.GetEpisodesAsync(show.Key, ct);

                landed &= show.All(wanted => episodes.Any(one =>
                    one.Season == wanted.Season && one.Number == wanted.Number && one.HasFile));
            }

            // Whether the server still has work in hand for this grab. Asked
            // once and used twice: to read a file the encoder wrote but filed
            // against the wrong row, and to keep from deleting a download a job
            // is still reading.
            EncodeJob? standing = await StandingAsync(sent, ct);

            if (!landed && standing is { State: EncodeJobState.Finished })
            {
                landed = await WroteItAnywayAsync(sent, thisTick, ct);
            }

            if (!landed)
            {
                await StillWaitingAsync(sent, thisTick, ct);

                continue;
            }

            // Both, never one. The library having every episode says the encode
            // landed; the job still running says the server is not finished
            // with the file it was given, and a download taken away under a job
            // that is still reading it is the fault that cost the owner 36 GB.
            // Where the server cannot say, the library is the proof and it is
            // the stronger of the two.
            if (standing is { State: EncodeJobState.Queued or EncodeJobState.Running })
            {
                await StillWaitingAsync(sent, thisTick, ct);

                continue;
            }

            // Every one of them. A pack staged nine and recorded one, so
            // eight were left in the intake folder for the sweep to puzzle over
            // on every tick after.
            foreach (string path in sent.StagedPaths)
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
    /// Adds the show a torrent names, so its episodes can be dispatched.
    /// </summary>
    /// <remarks>
    /// <para>
    /// An encode is asked for by the server's own episode id, and a show that
    /// is in no library has no episodes and no ids — so a pack for one could
    /// not be dispatched at all, however plainly its files named it. This looks
    /// the show up with the server's own metadata providers and imports it into
    /// the library its files read as, which is exactly what the dashboard does
    /// when a person adds content.
    /// </para>
    /// <para>
    /// <strong>Asked once per run.</strong> The import runs on the server's own
    /// queue and a show does not appear the moment it is dispatched, so a tick a
    /// minute later still finds it in no library — and without this that
    /// dispatched the same import again, and again, for as long as the queue
    /// took.
    /// </para>
    /// <para>
    /// False where there is no library of that kind, where the plugin cannot
    /// reach the server's providers, or where no provider knows the show. The
    /// caller then says which show it holds and leaves it alone.
    /// </para>
    /// </remarks>
    private async Task<bool> AddedAsync(
        IReadOnlyList<TorrentFile> files,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        if (imports is null || Staging.Claims(files) is not { } claimed)
        {
            return false;
        }

        if (!_added.Add(claimed.Title))
        {
            return true;
        }

        LibraryKind kind = Staging.Reads(files);

        Library? into = (await thisTick.GetLibrariesAsync(ct)).FirstOrDefault(one => one.Kind == kind);

        if (into is null)
        {
            // Asked for again on the next run rather than remembered as done:
            // there is nothing to wait for, and the answer changes the day the
            // owner makes a library of that kind.
            _added.Remove(claimed.Title);

            return false;
        }

        if (await imports.AddAsync(claimed.Title, claimed.Year, into, ct) is not string added)
        {
            _added.Remove(claimed.Title);

            return false;
        }

        journal.Finished(
            ActivityStage.Download,
            claimed.Title,
            $"was in no library, so it was looked up and added to {into.Name} as {added}; "
            + "its episodes are dispatched on the next pass");

        return true;
    }

    /// <summary>Shows this run has already asked the server to add.</summary>
    /// <remarks>
    /// Held rather than written down: a restart is a good enough reason to ask
    /// again, and by then either the import finished — in which case the show
    /// is in a library and this is never reached — or it did not, and asking
    /// once more is the right thing.
    /// </remarks>
    private readonly HashSet<string> _added = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a finished encode arrived under a row that is not the episode's.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Asked only of a job the server has said is finished, and only when the
    /// episode still shows no file. The encoder names what it writes after the
    /// episode it was asked for, so a file in the show's folders carrying this
    /// season and episode is this episode, whatever row the registration
    /// attached it to.
    /// </para>
    /// <para>
    /// On 1 September 2026 South Park S15E12 was dispatched with the server's
    /// own id for it, the encoder logged <c>for 153823</c> and wrote
    /// <c>South.Park.S15E12.1%.NoMercy.m3u8</c>, and the registration attached
    /// that file to episode <c>153785</c> — season 0. So the real S15E12 had no
    /// file, the queue was empty, and the plugin showed "encoding" for six hours
    /// before giving up and downloading the same episode a second time.
    /// </para>
    /// <para>
    /// Said out loud when it is taken. The download is about to be deleted on
    /// the strength of it, and an owner whose dashboard shows the episode under
    /// the wrong season deserves the sentence that explains it.
    /// </para>
    /// </remarks>
    private async Task<bool> WroteItAnywayAsync(
        StoredDownload sent,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        List<EpisodeKey> misfiled = [];

        foreach (IGrouping<int, EpisodeKey> show in sent.Covers.GroupBy(one => one.ShowId))
        {
            IReadOnlyList<Episode> episodes = await thisTick.GetEpisodesAsync(show.Key, ct);
            IReadOnlyList<string> files = await thisTick.GetFilesAsync(show.Key, ct);

            foreach (EpisodeKey wanted in show)
            {
                if (episodes.Any(one =>
                        one.Season == wanted.Season && one.Number == wanted.Number && one.HasFile))
                {
                    continue;
                }

                if (!Landed.Wrote(wanted, files))
                {
                    return false;
                }

                misfiled.Add(wanted);
            }
        }

        journal.Finished(
            ActivityStage.Dispatch,
            sent.ReleaseTitle,
            $"the encode finished and the server filed {string.Join(", ", misfiled)} under another episode; "
            + "the file is in the library under the right name, so this is done");

        logger.LogWarning(
            "{Release}: the encode finished and the server registered the file against another episode "
            + "than {Episodes}. The file is there under the right name.",
            sent.ReleaseTitle,
            string.Join(", ", misfiled));

        return true;
    }

    /// <summary>
    /// Says which show a torrent holds that the owner has not got, and leaves
    /// it where it is.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written down, not only said. A torrent that sits at finished for a week
    /// with no reason anywhere is exactly what this plugin exists not to do, and
    /// the journal is memory: the History page is where it stays.
    /// </para>
    /// <para>
    /// <strong>It names the show, because that is the owner's next move.</strong>
    /// "Nothing in it names an episode of a show in a library" is true and
    /// leaves them nowhere. The providers' own spelling where the server offers
    /// them — <em>Dark Matter (2024)</em> — is the name typed into Add content;
    /// the file's own spelling where it does not.
    /// </para>
    /// <para>
    /// <strong>The providers are asked once.</strong> This runs on every tick
    /// for as long as the torrent sits there, which is once a minute, and the
    /// answer cannot change without the show being added — at which point this
    /// is never reached again. The words are kept with the torrent and said
    /// again from memory.
    /// </para>
    /// </remarks>
    private async Task UnplaceableAsync(
        StoredDownload finished,
        IReadOnlyList<TorrentFile> files,
        CancellationToken ct)
    {
        if (!_unplaceable.TryGetValue(finished.InfoHash, out string? reason))
        {
            reason = Reason(files);

            _unplaceable[finished.InfoHash] = reason;

            await grabs.NotedAsync(
                finished.ReleaseTitle,
                reason,
                (time ?? TimeProvider.System).GetUtcNow(),
                ct);
        }

        journal.Failed(ActivityStage.Download, finished.ReleaseTitle, reason);
    }

    /// <summary>Why a torrent was left alone, in words the owner can act on.</summary>
    private static string Reason(IReadOnlyList<TorrentFile> files)
    {
        IReadOnlyList<string> named = Staging.Names(files);

        if (named.Count == 0)
        {
            return "nothing in it names an episode at all, so it was left where it is";
        }

        return $"it holds episodes of {string.Join(", ", named)}, which is in no library, "
            + "so it was left where it is — add the show and it will be taken on";
    }

    /// <summary>The job ids a grab is waiting on, or the ones for one episode of it.</summary>
    /// <remarks>
    /// A tagged id is <c>showXseasonXnumber:job</c>; an untagged one is from a
    /// row written before a grab could hold more than one, and answers for
    /// whatever it is asked about.
    /// </remarks>
    private static IEnumerable<string> Named(string column, EpisodeKey? episode)
    {
        foreach (string part in column.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int colon = part.IndexOf(':', StringComparison.Ordinal);

            if (colon < 0)
            {
                yield return part;

                continue;
            }

            if (episode is null
                || string.Equals(part[..colon], GrabRepository.Tag(episode.Value), StringComparison.Ordinal))
            {
                yield return part[(colon + 1)..];
            }
        }
    }

    /// <summary>Which of a grab's staged files is the one for this episode.</summary>
    /// <remarks>
    /// By the numbers in its own name, which is what the stager wrote it under.
    /// A grab of one episode has one file and it is that one; a pack has an
    /// episode per file, and pointing every dispatch at the first of them would
    /// put the same video in the library nine times over.
    /// </remarks>
    private static string? Staged(StoredDownload grab, EpisodeKey episode)
    {
        if (grab.StagedPaths.Count == 1 && grab.Covers.Count == 1)
        {
            return grab.StagedPaths[0];
        }

        return grab.StagedPaths.FirstOrDefault(one => Landed.Wrote(episode, [one]));
    }

    /// <summary>Torrents already reported as naming no show the owner has, and the words used.</summary>
    /// <remarks>
    /// Per run, like the encode clock beside it. A restart says it once more,
    /// which is a line on a page rather than a fault. The words are kept as
    /// well as the fact, because working them out asks the server's metadata
    /// providers and a tick a minute must not do that.
    /// </remarks>
    private readonly Dictionary<string, string> _unplaceable = new(StringComparer.OrdinalIgnoreCase);

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
    private async Task StillWaitingAsync(StoredDownload sent, LibraryThisTick thisTick, CancellationToken ct)
    {
        DateTimeOffset now = (time ?? TimeProvider.System).GetUtcNow();

        // Whether the server says the job it queued is still going. Where it
        // does, the file is not staged for a second time whatever the clock
        // below decides.
        bool alive = false;

        // Asked rather than inferred, where there is a job to ask about and a
        // server that answers. Everything below this sees one thing only —
        // whether the library has the episode yet — so an encode that died in
        // its first minute and one still running look the same, and both are
        // waited out for six hours before the episode goes back to missing and
        // the same gigabytes are downloaded again. media-server #31, which this
        // plugin opened, is what makes the difference sayable.
        {
            // Episode by episode, because a pack is nine encodes and one of them
            // failing says nothing about the other eight. It used to fail the
            // whole grab: on 1 September 2026 episode one's encode died, the
            // grab went with it, and a minute later the sweep took the staged
            // files of all nine — including episode five's, which was between
            // its first and second bundle and had been encoding happily.
            List<EpisodeKey> lost = [];

            foreach (EpisodeKey episode in sent.Covers)
            {
                if (await StandingAsync(sent, episode, ct) is not { State: EncodeJobState.Failed } dead)
                {
                    continue;
                }

                string why = dead.Failure ?? "the server gave up on the encode and said no more than that";

                lost.Add(episode);

                await grabs.UncoverAsync(sent.InfoHash, episode, ct);

                logger.LogWarning("{Release} {Episode}: {Reason}", sent.ReleaseTitle, episode, why);
                journal.Failed(ActivityStage.Dispatch, $"{sent.ReleaseTitle} {episode}", why);
            }

            if (lost.Count > 0 && lost.Count == sent.Covers.Count)
            {
                // Every one of them, so the release itself is the fault and is
                // refused for a while. One episode of nine is not.
                _waiting.Remove(sent.InfoHash);

                string said = $"every encode this release was asked for failed, the last of them for {lost.Count} episodes";

                await grabs.FailedAsync(sent.InfoHash, said, now, now + RefusedFor, ct);

                logger.LogWarning("{Release}: {Reason}", sent.ReleaseTitle, said);
                journal.Failed(ActivityStage.Dispatch, sent.ReleaseTitle, said);

                return;
            }

            if (lost.Count > 0)
            {
                // The rest of the pack carries on, and the tick that follows
                // reads the covers without the episodes that died.
                return;
            }

            EncodeJob? standing = await StandingAsync(sent, ct);

            if (standing is { State: EncodeJobState.Queued or EncodeJobState.Running })
            {
                // Alive, so it is not asked for a second time — and that is
                // the whole of what this branch does. The clock is started if
                // it is not running and never restarted, and then the six hours
                // below are left to run: an encoder that hangs reports Running
                // for ever, so a branch that returned here, or wrote the clock
                // on every tick, would leave a grab waiting for ever on a job
                // nothing was doing. That is the one thing the six hours are
                // there to stop.
                alive = true;

                _waiting.TryAdd(sent.InfoHash, now);
            }
        }

        if (!_waiting.TryGetValue(sent.InfoHash, out DateTimeOffset since) && !alive)
        {
            // Dispatched by a run of the plugin that is over. Its job is very
            // likely over with it: the owner's queue was empty while eleven
            // grabs waited on jobs the encoder had already thrown away, and
            // nothing here can see the queue to tell one that died from one
            // still running.
            //
            // So it is asked for once more and then waited on properly. The
            // file is already staged, so it costs one dispatch; the other way
            // round is six hours of waiting, the episode back to missing, and
            // the same gigabytes downloaded a second time.
            //
            // If the old job did survive the restart this makes a second one.
            // That is the lesser fault, and the one the plugin can undo — the
            // library having the episode ends both.
            _waiting[sent.InfoHash] = now;

            foreach (EpisodeKey episode in sent.Covers)
            {
                if (Staged(sent, episode) is string staged && File.Exists(staged))
                {
                    await DispatchAsync(sent.InfoHash, episode, staged, thisTick, ct);
                }
            }

            return;
        }

        if (now - since < Patience)
        {
            return;
        }

        _waiting.Remove(sent.InfoHash);

        string reason =
            $"the encode was asked for {Patience.TotalHours:0} hours ago and the library still does not have it";

        await grabs.FailedAsync(sent.InfoHash, reason, now, now + RefusedFor, ct);

        logger.LogWarning("{Release}: {Reason}", sent.ReleaseTitle, reason);
        journal.Failed(ActivityStage.Dispatch, sent.ReleaseTitle, reason);
    }

    /// <summary>
    /// What the server says about every encode a grab is waiting on, as one
    /// answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One for a grab the plugin named an episode for, and one per file for a
    /// pack handed over to be identified — nine of them for a season. They are
    /// held in the one column space-separated, which a job id never contains,
    /// so a row written when a grab could only have one still reads as itself.
    /// </para>
    /// <para>
    /// Failed if any failed, because one dead encode is the answer whatever the
    /// others are doing; otherwise still going if any is; finished only when
    /// every one of them is. Null where nothing can be said — no server to ask,
    /// no job named, or a job the server no longer knows — and null is never
    /// "finished": a pack is deleted on that answer.
    /// </para>
    /// </remarks>
    private Task<EncodeJob?> StandingAsync(StoredDownload sent, CancellationToken ct)
    {
        return StandingAsync(sent, episode: null, ct);
    }

    /// <summary>
    /// The same, about one episode of a pack.
    /// </summary>
    /// <remarks>
    /// Each dispatch writes its job down against the episode it was for, so a
    /// failure can be laid at that episode and the other eight can carry on.
    /// A row written before the tags carries a bare job id and answers for the
    /// whole grab, which is what it always meant.
    /// </remarks>
    private async Task<EncodeJob?> StandingAsync(StoredDownload sent, EpisodeKey? episode, CancellationToken ct)
    {
        if (jobs is null || sent.EncodeJobId is not string named)
        {
            return null;
        }

        EncodeJob? going = null;
        bool asked = false;

        foreach (string job in Named(named, episode))
        {
            asked = true;

            EncodeJob? standing = await jobs.StatusAsync(job, ct);

            if (standing is null)
            {
                // A job this server does not know. It cannot be called finished
                // and it cannot be called failed, so the whole grab is unknown.
                return null;
            }

            if (standing.State == EncodeJobState.Failed)
            {
                return standing;
            }

            if (standing.State != EncodeJobState.Finished)
            {
                going = standing;
            }
        }

        return asked ? going ?? new EncodeJob(EncodeJobState.Finished, null) : null;
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
