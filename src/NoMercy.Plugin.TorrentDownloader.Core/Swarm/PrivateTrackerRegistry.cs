// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Swarm;

/// <summary>
/// A private tracker the user added on purpose.
///
/// <para>
/// Nothing becomes private by accident. This is a deliberate entry with its own
/// credentials, and the only way a torrent can ever be uploaded from this plugin.
/// </para>
/// </summary>
public sealed record PrivateTracker
{
    public required string Name { get; init; }

    /// <summary>Usually carries the user's passkey, which is why it is never logged whole.</summary>
    public required string AnnounceUrl { get; init; }

    /// <summary>For the Torznab search path, where a tracker wants a separate key.</summary>
    public string? ApiKey { get; init; }

    /// <summary>
    /// Off by default. A private tracker without a ratio is an account that stops
    /// working, so turning this on is how a user keeps one alive - knowingly.
    /// </summary>
    public bool Seed { get; init; }

    public double SeedRatioTarget { get; init; } = 1.0;

    public TimeSpan SeedTimeTarget { get; init; } = TimeSpan.FromHours(72);

    public TorrentOrigin Origin => Seed ? TorrentOrigin.PrivateSeeding : TorrentOrigin.PrivateWithoutSeeding;
}

/// <summary>
/// Decides what a torrent is, from the trackers it announces to.
///
/// <para>
/// Everything is public unless one of its trackers is an entry the user added. That
/// default is the important part: a torrent whose origin cannot be established never
/// uploads.
/// </para>
/// </summary>
public sealed class PrivateTrackerRegistry
{
    private readonly Dictionary<string, PrivateTracker> _byHost = [];

    public PrivateTrackerRegistry(IEnumerable<PrivateTracker> trackers)
    {
        foreach (PrivateTracker tracker in trackers)
        {
            if (!Uri.TryCreate(tracker.AnnounceUrl, UriKind.Absolute, out Uri? parsed))
                throw new ArgumentException($"'{tracker.Name}' has an announce URL that cannot be read: {tracker.AnnounceUrl}", nameof(trackers));

            _byHost[parsed.Host.ToLowerInvariant()] = tracker;
        }
    }

    public TorrentOrigin OriginFor(IEnumerable<string> trackerUrls)
    {
        TorrentOrigin best = TorrentOrigin.Public;

        foreach (PrivateTracker matched in Matching(trackerUrls))
        {
            // A torrent listed on two private trackers, one of which we seed on:
            // seeding satisfies both accounts, so the more generous answer wins.
            if (matched.Origin == TorrentOrigin.PrivateSeeding)
                return TorrentOrigin.PrivateSeeding;

            best = TorrentOrigin.PrivateWithoutSeeding;
        }

        return best;
    }

    /// <summary>The base policy, with the matched tracker's own targets applied.</summary>
    public SwarmPolicy PolicyFor(SwarmPolicy basePolicy, IEnumerable<string> trackerUrls)
    {
        PrivateTracker? seeding = Matching(trackerUrls).FirstOrDefault(tracker => tracker.Seed)
            ?? Matching(trackerUrls).FirstOrDefault();

        return seeding is null
            ? basePolicy
            : basePolicy with
            {
                SeedRatioTarget = seeding.SeedRatioTarget,
                SeedTimeTarget = seeding.SeedTimeTarget,
            };
    }

    /// <summary>
    /// Matched on host. A passkey differs per user and on some trackers per torrent,
    /// so the whole URL identifies a request while the host identifies the tracker.
    /// </summary>
    private IEnumerable<PrivateTracker> Matching(IEnumerable<string> trackerUrls)
    {
        foreach (string url in trackerUrls)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
                continue;

            if (_byHost.TryGetValue(parsed.Host.ToLowerInvariant(), out PrivateTracker? tracker))
                yield return tracker;
        }
    }
}
