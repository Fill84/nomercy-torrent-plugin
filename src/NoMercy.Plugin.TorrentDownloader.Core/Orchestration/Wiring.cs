// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

/// <summary>
/// Choosing between candidates, using the profiles and scorer built in stage 0a.
///
/// <para>
/// The blacklist sets are left empty on purpose: the orchestrator already dropped
/// blacklisted releases before this is called, and asking twice means two places that
/// can disagree about what is skipped.
/// </para>
/// </summary>
public sealed class ProfileReleaseChooser(ReleaseProfile profile) : IReleaseChooser
{
    private static readonly IReadOnlySet<string> Nothing = new HashSet<string>();

    private readonly ReleaseDecider _decider = new();

    public ReleaseInfo? Choose(WantedEpisode episode, IReadOnlyList<ReleaseInfo> candidates, bool allowSeasonPacks)
    {
        if (candidates.Count == 0)
            return null;

        // The profile carries the owner's standing preference; the caller carries what
        // this particular search can justify. A pack is refused when either says no.
        ReleaseProfile effective = profile.AllowSeasonPacks && allowSeasonPacks
            ? profile
            : profile with { AllowSeasonPacks = false };

        FilterContext filter = new(
            episode.ShowTitle,
            new EpisodeSlot(episode.Key.Season, episode.Key.Episode),
            effective,
            Nothing,
            Nothing);

        return _decider.PickBest(candidates, filter, new ScoreContext(effective, null))?.Release;
    }
}

/// <summary>
/// Searching, over the indexer aggregator built in stage 0b.
///
/// <para>
/// The aggregator reports what every source returned and what every source refused, and
/// this used to keep the first half and drop the second on the floor. That is how two
/// sources sat behind a Cloudflare check for weeks looking exactly like two sources with
/// nothing to offer. <paramref name="report"/> is where the whole answer goes.
/// </para>
/// </summary>
public sealed class AggregatorReleaseSearch(
    IndexerAggregator aggregator,
    Func<AggregateResult, CancellationToken, Task>? report = null) : IReleaseSearch
{
    public async Task<IReadOnlyList<ReleaseInfo>> SearchAsync(SearchQuery query, CancellationToken ct)
    {
        AggregateResult result = await aggregator.SearchAsync(query, ct);

        if (report is not null)
            await report(result, ct);

        return result.Releases;
    }
}

/// <summary>
/// Reading the feed indexers, over the same aggregator the search uses.
///
/// <para>
/// The query is empty because a feed has no query: an RSS endpoint hands over whatever it
/// has posted lately and ignores what it was asked. Only feed indexers belong in here for
/// exactly that reason - an empty query put to a Torznab endpoint is a request for its
/// entire catalogue, which is a good way to be banned from it.
/// </para>
/// </summary>
public sealed class IndexerReleaseFeed(
    IndexerAggregator aggregator,
    Func<AggregateResult, CancellationToken, Task>? report = null) : IReleaseFeed
{
    public async Task<IReadOnlyList<ReleaseInfo>> LatestAsync(CancellationToken ct)
    {
        AggregateResult result = await aggregator.SearchAsync(new SearchQuery(string.Empty, null), ct);

        if (report is not null)
            await report(result, ct);

        return result.Releases;
    }
}

/// <summary>Fetching a <c>.torrent</c> an indexer pointed at.</summary>
public sealed class HttpTorrentFileFetcher(HttpClient client) : ITorrentFileFetcher
{
    /// <summary>No real torrent file is anywhere near this. The cap stops a bad URL becoming a memory problem.</summary>
    private const int MaxTorrentFileBytes = 8 * 1024 * 1024;

    public async Task<byte[]> FetchAsync(string url, CancellationToken ct)
    {
        using HttpResponseMessage response = await client.GetAsync(url, ct);

        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength > MaxTorrentFileBytes)
            throw new InvalidOperationException($"{url} offered {response.Content.Headers.ContentLength} bytes for a torrent file");

        byte[] contents = await response.Content.ReadAsByteArrayAsync(ct);

        return contents.Length <= MaxTorrentFileBytes
            ? contents
            : throw new InvalidOperationException($"{url} returned {contents.Length} bytes for a torrent file");
    }
}

/// <summary>
/// Moving a finished download into the folder the owner nominated.
///
/// <para>
/// One folder per download, named after the release, rather than the video files loose
/// in the finished folder. The server scans a folder and takes the first media folder it
/// finds, so two downloads sitting side by side would make it pick one and import the
/// wrong show. The folder name is also what the title lookup reads.
/// </para>
///
/// <para>
/// Returns the folder it created, or null when the move did not happen - which leaves
/// the grab unfinished so the next cycle tries again. Nothing here throws: an incomplete
/// handoff is never recorded as a finished one.
/// </para>
/// </summary>
public sealed class FinishedFolderMover(string finishedFolder)
{
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".mpg", ".mpeg",
    };

    /// <summary>Samples and extras are not the episode. Moving them makes the server import junk.</summary>
    private const long SmallestPlausibleEpisodeBytes = 50 * 1024 * 1024;

    /// <param name="completedPath">
    /// Where the engine put the torrent's content. A folder for a multi-file torrent, and
    /// the file itself for a single-file one - a torrent's "name" is a directory in the
    /// first case and a filename in the second, and the engine honestly reports both.
    /// </param>
    public async Task<string?> MoveAsync(string completedPath, CancellationToken ct)
    {
        try
        {
            // Single file. This method began by testing Directory.Exists and giving up, so
            // on a real server three finished episodes sat at 100% in the download folder
            // and were retried every minute forever: the move never happened, so no encode
            // was ever queued and nothing reached the library. Every one of them was a
            // single-file torrent, which is what most episode releases are.
            if (File.Exists(completedPath))
            {
                FileInfo only = new(completedPath);

                if (!VideoExtensions.Contains(only.Extension))
                    return null;

                string singleDestination = Path.Combine(finishedFolder, Path.GetFileNameWithoutExtension(only.Name));
                Directory.CreateDirectory(singleDestination);

                string target = Path.Combine(singleDestination, only.Name);

                if (!File.Exists(target))
                    only.MoveTo(target);

                await Task.CompletedTask;

                return singleDestination;
            }

            if (!Directory.Exists(completedPath))
                return null;

            List<FileInfo> videos =
            [
                .. new DirectoryInfo(completedPath)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Where(file => VideoExtensions.Contains(file.Extension))
                    .Where(file => file.Length >= SmallestPlausibleEpisodeBytes || file.Length == 0)
                    .OrderByDescending(file => file.Length),
            ];

            if (videos.Count == 0)
                return null;

            // Named after the download, because that name is the release name and the
            // server reads it to work out what this is.
            string destinationFolder = Path.Combine(finishedFolder, new DirectoryInfo(completedPath).Name);
            Directory.CreateDirectory(destinationFolder);

            foreach (FileInfo video in videos)
            {
                string destination = Path.Combine(destinationFolder, video.Name);

                // Never overwrite. Something already there is either this file from a
                // half-finished earlier attempt or somebody else's, and both are worse
                // to clobber than to leave alone.
                if (File.Exists(destination))
                    continue;

                video.MoveTo(destination);
            }

            await Task.CompletedTask;

            return destinationFolder;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
