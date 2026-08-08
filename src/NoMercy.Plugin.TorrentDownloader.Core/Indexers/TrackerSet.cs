// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Indexers;

/// <summary>
/// Every tracker anyone named for one torrent.
///
/// <para>
/// The same release is listed on many sites, and each magnet announces a different
/// tracker set. The aggregator used to pick one result and drop the rest, which threw
/// away the useful part: one info hash is one torrent, and it deserves the union. A
/// bigger swarm is a faster download, which is the whole requirement.
/// </para>
/// </summary>
public static class TrackerSet
{
    public static IReadOnlyList<string> Merge(IEnumerable<ReleaseInfo> sameTorrent)
    {
        List<string> merged = [];
        HashSet<string> seen = [];

        foreach (ReleaseInfo release in sameTorrent)
        {
            if (release.MagnetUri is not string magnet || !MagnetLink.TryParse(magnet, out MagnetLink? parsed))
                continue;

            foreach (string tracker in parsed.Trackers)
            {
                // First spelling wins, so the order a user sees follows indexer priority
                // rather than whichever site happened to answer first.
                if (seen.Add(Key(tracker)))
                    merged.Add(tracker);
            }
        }

        return merged;
    }

    /// <summary>
    /// What makes two spellings the same tracker. Scheme and port stay significant -
    /// a tracker reachable over UDP and over HTTP is two ways in, not one written
    /// twice - while case and a trailing slash are noise.
    /// </summary>
    private static string Key(string tracker)
    {
        string trimmed = tracker.Trim().TrimEnd('/');

        return Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? parsed)
            ? $"{parsed.Scheme.ToLowerInvariant()}://{parsed.Host.ToLowerInvariant()}:{parsed.Port}{parsed.AbsolutePath.TrimEnd('/')}"
            : trimmed.ToLowerInvariant();
    }
}
