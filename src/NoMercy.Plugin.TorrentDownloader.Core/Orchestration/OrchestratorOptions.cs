// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

public sealed record OrchestratorOptions
{
    /// <summary>
    /// How many downloads may run at once. This is the bound that turns a first run on
    /// a library with years of gaps into a steady stream rather than several hundred
    /// downloads competing for one connection, one disk and one swarm.
    /// </summary>
    public int MaxConcurrentDownloads { get; init; } = 5;


    /// <summary>
    /// How many episodes of a season have to be missing before a season pack is worth
    /// considering.
    ///
    /// <para>
    /// A pack costs a whole season of bytes whether it settles one gap or ten, so below
    /// this the episode release is the cheaper answer by a wide margin. Three is the
    /// point where re-downloading a season stops being obviously worse than three
    /// separate torrents; it is a judgement, which is why it is a setting.
    /// </para>
    /// </summary>
    public int SeasonPackThreshold { get; init; } = 3;

    /// <summary>After this many fruitless searches an episode is parked rather than asked for forever.</summary>
    public int MaxSearchAttempts { get; init; } = 12;

    /// <summary>How long a failed release is skipped before it is worth another try.</summary>
    public TimeSpan BlacklistDuration { get; init; } = TimeSpan.FromDays(14);

    /// <summary>
    /// Whether season 0 counts.
    ///
    /// <para>
    /// Off, because specials are where a library's metadata is loosest - recaps,
    /// behind-the-scenes reels, convention panels - and they sort to the front of an
    /// unsearched queue, so the first thing the plugin would ever download is the thing
    /// the owner wanted least. On a real library this was twenty-five Simpsons specials
    /// ahead of every actual episode.
    /// </para>
    /// </summary>
    public bool IncludeSpecials { get; init; }

    /// <summary>
    /// Shows to follow even though nothing of them is on the server yet.
    ///
    /// <para>
    /// The counterpart of the shelf rule. Without it the plugin can only ever finish a
    /// show somebody already started by hand, and can never begin one - which is a
    /// perfectly coherent tool and not the one anybody wants.
    /// </para>
    /// </summary>
    public IReadOnlyList<int> FollowedShowIds { get; init; } = [];

    public required string DownloadFolder { get; init; }

    /// <summary>
    /// Trackers added to every torrent, on top of whatever the indexers named for it.
    ///
    /// <para>
    /// The aggregator already merges the trackers every indexer reported for one info hash,
    /// which is the bigger half of the swarm. These are the owner's own, and they go on
    /// everything rather than only on the magnets this plugin had to build - a torrent
    /// found through one site announces to that site's trackers alone, and the peers on the
    /// others are simply never asked.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> ExtraTrackers { get; init; } = [];

    /// <summary>
    /// How much room to leave on the disk after a download finishes.
    ///
    /// <para>
    /// A media server that fills its own disk does not politely stop downloading - it
    /// stops encoding, stops writing databases, and stops playing back, all at once and
    /// for reasons that look nothing like a torrent. Twenty gigabytes is roughly one
    /// oversized film of headroom, and cheap insurance against a plugin that would
    /// otherwise cheerfully take the last byte.
    /// </para>
    /// </summary>
    public long MinimumFreeBytes { get; init; } = 20L * 1024 * 1024 * 1024;
}
