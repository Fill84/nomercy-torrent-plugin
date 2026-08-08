// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

/// <summary>Searching, behind an interface so a test does not need indexers.</summary>
public interface IReleaseSearch
{
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct);
}

/// <summary>
/// Choosing between candidates. Wraps the profiles and the scorer, which is the
/// orchestrator's business to call and nobody's business to reimplement.
/// </summary>
public interface IReleaseChooser
{
    ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates);
}

/// <summary>Handing a finished download to the server's import pipeline.</summary>
public interface IIntakeHandoff
{
    /// <summary>False when the move did not happen, which leaves the grab unfinished so the next cycle retries it.</summary>
    Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct);
}

public sealed record OrchestratorOptions
{
    /// <summary>
    /// How many downloads may run at once. This is the bound that turns a first run on
    /// a library with years of gaps into a steady stream rather than several hundred
    /// downloads competing for one connection, one disk and one swarm.
    /// </summary>
    public int MaxConcurrentDownloads { get; init; } = 5;

    /// <summary>How many wanted episodes to search per cycle. Indexers are rate limited; a cycle that asks for everything gets throttled.</summary>
    public int SearchBatchSize { get; init; } = 10;

    /// <summary>After this many fruitless searches an episode is parked rather than asked for forever.</summary>
    public int MaxSearchAttempts { get; init; } = 12;

    /// <summary>How long a failed release is skipped before it is worth another try.</summary>
    public TimeSpan BlacklistDuration { get; init; } = TimeSpan.FromDays(14);

    public required string DownloadFolder { get; init; }
}

/// <summary>
/// The loop: notice what is missing, find something for it, hand it to the engine,
/// and put the result where the server will import it.
///
/// <para>
/// Each phase is a separate method rather than one long cycle, because they run on
/// different triggers - a library change refreshes, a cron searches, and transfers are
/// polled - and because a test that wants to prove one of them should not have to
/// arrange the other two.
/// </para>
/// </summary>
public sealed class DownloadOrchestrator(
    ILibraryQuery library,
    IDownloadStore store,
    IReleaseSearch search,
    IReleaseChooser chooser,
    ITorrentEngine engine,
    IIntakeHandoff intake,
    OrchestratorOptions options,
    PrivateTrackerRegistry privateTrackers,
    Func<DateTimeOffset> now)
{
    /// <summary>
    /// Brings the wanted list in line with the library. Everything is watched, so this
    /// is the whole of "which shows do we follow": the library is the list.
    /// </summary>
    public async Task<int> RefreshWantedAsync(CancellationToken ct)
    {
        List<WantedEpisode> missing = [];

        foreach (LibraryShow show in await library.GetShowsAsync(ct))
        {
            // A show with no folder cannot be a download target. Skipping it here beats
            // composing a path from null somewhere further down.
            if (show.Folder is null)
                continue;

            foreach (LibraryEpisode episode in await library.GetEpisodesAsync(show.ShowId, ct))
            {
                if (episode.HasFile)
                    continue;

                missing.Add(new WantedEpisode
                {
                    Key = new EpisodeKey(show.ShowId, episode.SeasonNumber, episode.EpisodeNumber),
                    ShowTitle = show.Title,
                    EpisodeTitle = episode.Title,
                    AirDate = episode.AirDate is DateTimeOffset aired ? DateOnly.FromDateTime(aired.UtcDateTime) : null,
                });
            }
        }

        await store.RefreshWantedAsync(missing, ct);

        return missing.Count;
    }

    /// <summary>Searches for what is wanted and grabs what is worth grabbing. Returns how many were handed to the engine.</summary>
    public async Task<int> SearchCycleAsync(CancellationToken ct)
    {
        int running = (await store.ActiveGrabsAsync(ct)).Count;
        int room = options.MaxConcurrentDownloads - running;

        if (room <= 0)
            return 0;

        int grabbed = 0;

        foreach (WantedEpisode episode in await store.WantedAsync(options.SearchBatchSize, ct))
        {
            if (grabbed >= room)
                break;

            if (await TryGrabAsync(episode, ct))
                grabbed++;
        }

        return grabbed;
    }

    private async Task<bool> TryGrabAsync(WantedEpisode episode, CancellationToken ct)
    {
        IReadOnlyList<ReleaseInfo> found = await search.SearchAsync(
            new SearchQuery(episode.ShowTitle, new EpisodeSlot(episode.Key.Season, episode.Key.Episode)),
            ct);

        List<ReleaseInfo> allowed = [];

        foreach (ReleaseInfo release in found)
        {
            if (!await store.IsBlacklistedAsync(release.InfoHash, release.Title, now(), ct))
                allowed.Add(release);
        }

        ReleaseInfo? chosen = chooser.Choose(episode, allowed);

        if (chosen is null)
        {
            // Nothing usable. Park it once it has been asked for often enough that the
            // answer is not going to change on the next cycle either.
            WantedState next = episode.SearchAttempts + 1 >= options.MaxSearchAttempts
                ? WantedState.Unavailable
                : WantedState.Wanted;

            await store.MarkSearchedAsync(episode.Key, now(), next, ct);
            return false;
        }

        string? source = chosen.MagnetUri ?? chosen.DownloadUrl;

        if (source is null)
        {
            await store.MarkSearchedAsync(episode.Key, now(), WantedState.Wanted, ct);
            return false;
        }

        string infoHash = await engine.AddAsync(new TorrentRequest
        {
            Source = source,
            DestinationFolder = options.DownloadFolder,
            Origin = privateTrackers.OriginFor(chosen.Trackers),
            ExtraTrackers = chosen.Trackers,
        }, ct);

        await store.AddGrabAsync(new Grab
        {
            InfoHash = infoHash,
            Key = episode.Key,
            ReleaseTitle = chosen.Title,
            Indexer = chosen.IndexerName,
            SizeBytes = chosen.SizeBytes,
            GrabbedAt = now(),
        }, ct);

        await store.MarkSearchedAsync(episode.Key, now(), WantedState.Grabbed, ct);

        return true;
    }

    /// <summary>
    /// Polls the engine and acts on what changed. Returns how many downloads were
    /// handed to the intake this cycle.
    /// </summary>
    public async Task<int> TransfersCycleAsync(CancellationToken ct)
    {
        int imported = 0;

        foreach (EngineTransfer transfer in await engine.TransfersAsync(ct))
        {
            Grab? grab = await store.FindGrabAsync(transfer.InfoHash, ct);

            if (grab is null)
                continue;

            await store.RecordTransferAsync(new Transfer
            {
                InfoHash = transfer.InfoHash,
                BytesDone = transfer.BytesDone,
                BytesTotal = transfer.BytesTotal,
                Peers = transfer.Peers,
                UpdatedAt = now(),
            }, ct);

            switch (transfer.State)
            {
                case EngineState.Downloading when grab.State == GrabState.Grabbed:
                    await store.UpdateGrabAsync(transfer.InfoHash, GrabState.Downloading, null, null, ct);
                    break;

                case EngineState.Completed when grab.State != GrabState.Imported:
                    if (await ImportAsync(grab, transfer, ct))
                        imported++;

                    break;

                case EngineState.Failed:
                    await FailAsync(grab, transfer, ct);
                    break;
            }
        }

        return imported;
    }

    private async Task<bool> ImportAsync(Grab grab, EngineTransfer transfer, CancellationToken ct)
    {
        if (transfer.CompletedFolder is null)
            return false;

        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Downloaded, null, null, ct);

        if (!await intake.MoveIntoIntakeAsync(transfer.CompletedFolder, grab.Key, ct))
        {
            // The move did not happen, so the grab stays unfinished and the next cycle
            // tries again. An incomplete handoff is never recorded as a finished one.
            return false;
        }

        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Imported, null, now(), ct);
        await store.MarkSearchedAsync(grab.Key, now(), WantedState.Done, ct);

        return true;
    }

    private async Task FailAsync(Grab grab, EngineTransfer transfer, CancellationToken ct)
    {
        if (grab.State == GrabState.Failed)
            return;

        await store.UpdateGrabAsync(grab.InfoHash, GrabState.Failed, transfer.FailureReason, now(), ct);

        await store.BlacklistAsync(new BlacklistEntry
        {
            InfoHash = grab.InfoHash,
            ReleaseTitle = grab.ReleaseTitle,
            Reason = transfer.FailureReason ?? "the download failed",
            AddedAt = now(),
            ExpiresAt = now() + options.BlacklistDuration,
        }, ct);

        // Wanted again, so the next cycle looks for a different release. The blacklist
        // is what stops it choosing the same broken one.
        await store.MarkSearchedAsync(grab.Key, now(), WantedState.Wanted, ct);

        await engine.RemoveAsync(grab.InfoHash, deleteFiles: true, ct);
    }
}
