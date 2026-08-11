// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

// What the orchestrator needs from the world, and the whole of it. Every one is
// implemented twice: once for real in the plugin shell, once as a double in the tests -
// which is what lets the download loop be tested without an indexer, a swarm or a server.
/// <summary>Searching, behind an interface so a test does not need indexers.</summary>
public interface IReleaseSearch
{
    Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct);
}

/// <summary>
/// Everything an indexer has posted lately, asked for without a query.
///
/// <para>
/// A feed is not a search. A search asks "who has this episode" and pays a rate limit per
/// question; a feed is handed over whole and costs one request no matter how much of it
/// turns out to be useful. That difference is the whole reason to have both: the search
/// cadence works through the whole backlog and then has nothing left to do, and the feed
/// catches tonight's airing within a quarter of an hour of it being posted, without asking
/// anybody anything.
/// </para>
/// </summary>
public interface IReleaseFeed
{
    Task<IReadOnlyList<ReleaseInfo>> LatestAsync(CancellationToken ct);
}

/// <summary>
/// Choosing between candidates. Wraps the profiles and the scorer, which is the
/// orchestrator's business to call and nobody's business to reimplement.
/// </summary>
public interface IReleaseChooser
{
    /// <summary>
    /// <paramref name="allowSeasonPacks"/> is the caller's decision, not this one's: only
    /// the orchestrator knows how much of the season is missing, and therefore whether a
    /// season's worth of bytes buys one gap or ten.
    /// </summary>
    ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates, bool allowSeasonPacks);
}

/// <summary>Handing a finished download to the server's import pipeline.</summary>
public interface IIntakeHandoff
{
    /// <summary>False when the move did not happen, which leaves the grab unfinished so the next cycle retries it.</summary>
    Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct);
}
