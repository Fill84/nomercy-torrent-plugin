// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

public class TorrentDownloaderSettings
{
    public string TransfersCron { get; set; } = "* * * * *";
    public string FeedCron { get; set; } = "*/15 * * * *";
    public string SearchCron { get; set; } = "0 */6 * * *";
    public string MaintenanceCron { get; set; } = "0 4 * * *";

    public string IncompleteFolder { get; set; } = string.Empty;
    public string IntakeFolder { get; set; } = string.Empty;

    // Season 0. Off, because specials sort to the front of a queue nothing has searched
    // yet, so they would be the first thing this plugin ever downloaded - and they are
    // where a library's metadata is loosest. See OrchestratorOptions.IncludeSpecials.
    public bool IncludeSpecials { get; set; }

    // The three questions an owner can actually answer about quality. The release profile
    // underneath has a dozen more knobs - codecs, groups, term rules, size bounds - and
    // none of them belongs on a page until somebody asks for it: a setting nobody
    // understands is a setting that gets set wrong and blamed on the plugin.
    //
    // A maximum rather than a preference. Above it is off the ladder entirely, because a
    // rung that exists is a rung the scorer can argue itself onto when the seeders look
    // good, and then a 2160p remux arrives on a connection that cannot carry it.
    public string MaximumResolution { get; set; } = "1080p";

    /// <summary>Below this a release is usually dead or a trap, and the download stalls at 2%.</summary>
    public int MinimumSeeders { get; set; } = 2;

    /// <summary>A pack is still only considered once enough of a season is missing to be worth its bytes.</summary>
    public bool AllowSeasonPacks { get; set; } = true;

    public List<IndexerSettings> Indexers { get; set; } = [];
    public List<TorrentClientSettings> Clients { get; set; } = [];
    public List<PrivateTrackerSettings> PrivateTrackers { get; set; } = [];

    // Set by SettingsSaveHandler on every successful save/add/remove, never by a form
    // field - it exists so the settings page has a visible sign a save actually reached
    // disk, since the client discards a successful action's response body entirely and
    // only re-fetches the view.
    public DateTimeOffset? LastSavedAtUtc { get; set; }
}

// No ApiKey here. It goes to IPluginSecretStore under the key this entry's Name
// produces, because IPluginConfiguration is whole-object JSON on disk and a key
// written through it would sit in plaintext next to the rest of the settings.
public class IndexerSettings
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "torznab";
    public string Url { get; set; } = string.Empty;
    public int Priority { get; set; } = 25;
    public bool Enabled { get; set; } = true;
    public int MinimumIntervalSeconds { get; set; } = 15;
    public List<string> Categories { get; set; } = [];
}

// No AnnounceUrl here, and that is not the same reason as the other two - it is a
// stronger one. An announce URL carries the user's passkey, so the whole URL is the
// secret, not a field beside it. What is left in configuration is a name, whether this
// tracker is on, and the seeding targets - none of which identifies an account.
//
// The consequence is deliberate: the settings page can say a URL is stored but never
// show it back, exactly as it treats an API key. A passkey that appears in a rendered
// page is a passkey in a browser cache, a screenshot and a support ticket.
//
// This is the only way a torrent from this plugin can ever be uploaded. Everything else
// is public and public never seeds, so an entry here is always a deliberate act.
public class PrivateTrackerSettings
{
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    /// <summary>Off by default: seeding is what keeps a private account alive, and it is the user's call to start.</summary>
    public bool Seed { get; set; }

    public double SeedRatioTarget { get; set; } = 1.0;
    public int SeedTimeTargetHours { get; set; } = 72;
}

// No Password here, for the same reason.
public class TorrentClientSettings
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "qbittorrent";
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
