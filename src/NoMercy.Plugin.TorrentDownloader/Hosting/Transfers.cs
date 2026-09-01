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
    ShowAdmission? admission = null)
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

    /// <summary>Shows this run has already asked the server to add.</summary>
    /// <remarks>
    /// Held rather than written down: a restart is a good enough reason to ask
    /// again, and by then either the import finished — in which case the show
    /// is in a library and this is never reached — or it did not, and asking
    /// once more is the right thing.
    /// </remarks>
    private readonly HashSet<string> _admitted = new(StringComparer.OrdinalIgnoreCase);

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
        List<string> justStaged = [];

        foreach (StoredDownload finished in plan.Stage)
        {
            if (await StageAsync(finished, incompleteFolder, intakeFolder, thisTick, ct) is string written)
            {
                justStaged.Add(written);
            }
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
            open.Select(one => one.StagedPath).OfType<string>().Concat(justStaged),
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

    /// <summary>
    /// Every torrent the client has given up on, blacklisted and put back.
    /// </summary>
    /// <remarks>
    /// Both halves and one transaction, which is the store's business. Without
    /// the blacklist the next cycle chooses the same release and fails the same
    /// way for as long as the plugin runs; without the return the episodes look
    /// grabbed for ever.
    /// </remarks>
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
    /// Where it put the episode, or null when it put nothing. The tick needs
    /// this: a file staged a moment ago is one something is waiting on, and
    /// reading the open grabs again was the other way of finding that out.
    /// </returns>
    private async Task<string?> StageAsync(
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
                    // episode has the id an encode is asked for by. Handing the
                    // files over without one asked the server to guess, and on
                    // 31 August 2026 it guessed nine times and wrote nothing.
                    //
                    // Nothing else happens this tick. The show arrives through
                    // the server's own import, and the next tick sees it like
                    // any other — matched by name, covered, staged, dispatched.
                    if (await AdmittedAsync(files, thisTick, ct))
                    {
                        return null;
                    }

                    // Only where that could not be done: the files go over as
                    // they are and the server is asked to work them out.
                    if (await IdentifiedAsync(finished, files, incompleteFolder, thisTick, ct))
                    {
                        return null;
                    }


                    // Written down, not only said. Nothing in it names an
                    // episode of a show the owner has — the usual cause being a
                    // pack pasted in for a show not added to a library yet —
                    // and guessing where to put it is worse than leaving it
                    // where the owner put it. It goes to the History page
                    // because the journal is memory: a torrent that sits at
                    // finished for a week with no reason anywhere is exactly
                    // what this plugin exists not to do.
                    //
                    // Once per run, and the tick that follows says nothing. The
                    // check is cheap and the answer changes the moment the show
                    // is added, so it goes on being asked; saying so every
                    // minute would bury the page it is written on.
                    // Naming what the files say they are, because the owner's
                    // next move is to add that show to a library and there is
                    // no other way for them to know which one. Left as "nothing
                    // in it names an episode of a show in a library" it is a
                    // true sentence that ends the conversation.
                    IReadOnlyList<string> named = Staging.Names(files);

                    string reason = named.Count == 0
                        ? "nothing in it names an episode at all, so it was left where it is"
                        : $"it holds episodes of {string.Join(", ", named)}, which is in no library, "
                          + "so it was left where it is — add the show and it will be taken on";

                    journal.Failed(ActivityStage.Download, finished.ReleaseTitle, reason);

                    if (_unplaceable.Add(finished.InfoHash))
                    {
                        await grabs.NotedAsync(
                            finished.ReleaseTitle,
                            reason,
                            (time ?? TimeProvider.System).GetUtcNow(),
                            ct);
                    }

                    return null;
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
                return null;
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
                await DispatchAsync(finished.InfoHash, one.File.Episode, one.Path!, thisTick, ct);
            }

            return first.Path;
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            logger.LogWarning("{Release} could not be staged: {Reason}", finished.ReleaseTitle, wrong.Message);
            journal.Failed(ActivityStage.Download, finished.ReleaseTitle, wrong.Message);

            return null;
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
            await grabs.EncodeJobAsync(infoHash, job, ct);
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

            foreach (EpisodeKey episode in staged.Covers)
            {
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
            if (await StandingAsync(sent, ct) is { State: EncodeJobState.Queued or EncodeJobState.Running })
            {
                await StillWaitingAsync(sent, thisTick, ct);

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
    /// False where there is no library of that kind, where the plugin cannot
    /// reach the server's providers, or where no provider knows the show. The
    /// caller then falls back to handing the files over as they are.
    /// </para>
    /// </remarks>
    private async Task<bool> AdmittedAsync(
        IReadOnlyList<TorrentFile> files,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        if (admission is null || Staging.Claims(files) is not { } claimed)
        {
            return false;
        }

        // Asked once. The import runs on the server's own queue and a show does
        // not appear the moment it is dispatched, so a tick a minute later
        // still finds it in no library — and without this that dispatched the
        // same import again, and again, for as long as the queue took.
        if (!_admitted.Add(claimed.Title))
        {
            return true;
        }

        LibraryKind kind = Staging.Reads(files);

        Library? into = (await thisTick.GetLibrariesAsync(ct)).FirstOrDefault(one => one.Kind == kind);

        if (into is null)
        {
            return false;
        }

        if (await admission.AddAsync(claimed.Title, claimed.Year, into, ct) is not string added)
        {
            return false;
        }

        journal.Finished(
            ActivityStage.Download,
            claimed.Title,
            $"was in no library, so it was looked up and added to {into.Name} as {added}; "
            + "its episodes are dispatched on the next pass");

        return true;
    }

    /// <summary>
    /// Hands a torrent's videos to a library for the server to identify.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only ever for a torrent added by hand: the search chain records the
    /// episodes it chose, so a finished grab covering none of them is one the
    /// owner pasted in. There is no episode row to name, so the server is asked
    /// to work the file out from its name — a guess, and the owner's own, since
    /// they asked for this torrent.
    /// </para>
    /// <para>
    /// <strong>From where they are, not from the intake folder.</strong> Staging
    /// them first would put nine files in a folder whose sweep deletes whatever
    /// no grab is waiting on, and a pack's files are named per episode while its
    /// grab is named for the season: they would not match, and the next tick
    /// would delete the download. The encoder takes a path and does not care
    /// which folder it is in.
    /// </para>
    /// <para>
    /// False where there is nowhere to put it, which is the case the caller then
    /// reports: no library of the kind the files read as.
    /// </para>
    /// </remarks>
    private async Task<bool> IdentifiedAsync(
        StoredDownload finished,
        IReadOnlyList<TorrentFile> files,
        string incompleteFolder,
        LibraryThisTick thisTick,
        CancellationToken ct)
    {
        LibraryKind kind = Staging.Reads(files);

        Library? into = (await thisTick.GetLibrariesAsync(ct)).FirstOrDefault(one => one.Kind == kind);

        if (into is null)
        {
            return false;
        }

        // Every job the server names, because every one of them is reading a
        // file out of the download folder and the pack cannot go until they are
        // all done with it.
        List<string> queued = [];

        bool any = false;

        foreach (Staged file in Staging.Wanted(files).Select(one => new Staged(one.Path, new(0, 0, 0), one.Length)))
        {
            string path = Path.Combine(
                incompleteFolder,
                file.Path.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(path))
            {
                continue;
            }

            EncodeAsk asked = await dispatch.IdentifyAsync(path, into, ct);

            if (!asked.Taken)
            {
                continue;
            }

            any = true;

            if (asked.JobId is string job)
            {
                queued.Add(job);
            }

            // No episode: there is none, and that is what was handed over.
            // A key of noughts here drew "Series S00E00" on the History page.
            await grabs.DispatchedAsync(
                null,
                null,
                Path.GetFileName(path),
                into.Name,
                (time ?? TimeProvider.System).GetUtcNow(),
                ct);
        }

        if (any)
        {
            // Done rather than dispatched, because there is nothing left for
            // this plugin to do and nothing it could wait for: it does not know
            // which episodes these files became, so it can never see them
            // arrive. Leaving it dispatched put it in front of a step that
            // deletes what it is finished with, and that step cost the owner
            // the same pack twice.
            await grabs.StateAsync(finished.InfoHash, GrabState.Done, ct);

            if (queued.Count > 0)
            {
                await grabs.EncodeJobAsync(finished.InfoHash, string.Join(' ', queued), ct);
            }

            _unplaceable.Remove(finished.InfoHash);

            // Said out loud, because the download stays where it is and the
            // owner is the one who decides when it goes.
            journal.Finished(
                ActivityStage.Dispatch,
                finished.ReleaseTitle,
                $"handed to {into.Name} for the server to identify; the download is left in place, "
                + "because this plugin cannot tell whether the server kept it");
        }

        return any;
    }

    /// <summary>Torrents already reported as naming no show the owner has.</summary>
    /// <remarks>
    /// Per run, like the encode clock beside it. A restart says it once more,
    /// which is a line on a page rather than a fault.
    /// </remarks>
    private readonly HashSet<string> _unplaceable = new(StringComparer.OrdinalIgnoreCase);

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
            EncodeJob? standing = await StandingAsync(sent, ct);

            if (standing is { State: EncodeJobState.Failed })
            {
                _waiting.Remove(sent.InfoHash);

                string said = standing.Failure ?? "the server gave up on the encode and said no more than that";

                await grabs.FailedAsync(sent.InfoHash, said, now, now + RefusedFor, ct);

                logger.LogWarning("{Release}: {Reason}", sent.ReleaseTitle, said);
                journal.Failed(ActivityStage.Dispatch, sent.ReleaseTitle, said);

                return;
            }

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

            if (sent.StagedPath is string staged && File.Exists(staged))
            {
                foreach (EpisodeKey episode in sent.Covers)
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
    private async Task<EncodeJob?> StandingAsync(StoredDownload sent, CancellationToken ct)
    {
        if (jobs is null || sent.EncodeJobId is not string named)
        {
            return null;
        }

        EncodeJob? going = null;
        bool asked = false;

        foreach (string job in named.Split(' ', StringSplitOptions.RemoveEmptyEntries))
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
