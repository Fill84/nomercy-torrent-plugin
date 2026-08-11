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
    /// <summary>
    /// Which video codec to accept: <c>h264</c>, <c>h265</c> or <c>any</c>.
    ///
    /// <para>
    /// The same three answers torrent-feed gives, and the same meanings. <c>h264</c>
    /// requires an explicit x264 / h264 / AVC tag, so an untagged release is refused rather
    /// than passing as "at least it is not HEVC" - which is the point, because an untagged
    /// rip is exactly where an unwanted codec hides. <c>h265</c> requires HEVC. <c>any</c>
    /// stops asking.
    /// </para>
    ///
    /// <para>
    /// Defaults to <c>any</c>, which is what this plugin did before the setting existed. An
    /// owner who does not want x265 says so; one who only wants x265 says that instead.
    /// </para>
    /// </summary>
    public string Codec { get; set; } = "any";

    /// <summary>
    /// Refuse anything that is not English audio only.
    ///
    /// <para>
    /// On by default, and it means what it says: a release carrying a second language is
    /// refused even when English is one of them. <c>MULTI</c>, <c>ITA.ENG</c> and
    /// <c>FR.ENG</c> all go, along with foreign episode numbering like <c>Cap.101</c>.
    /// Turned off, no language rule is applied at all - which is the answer for a library
    /// that is not English.
    /// </para>
    /// </summary>
    public bool EnglishOnly { get; set; } = true;

    /// <summary>
    /// Words that disqualify a release outright, whatever else is right about it.
    ///
    /// <para>
    /// The escape hatch for what no rule can express: a release group whose rips are
    /// broken, a tag this library never wants. Matched as plain text against the release
    /// title, case-insensitively.
    /// </para>
    ///
    /// <para>
    /// Empty by default. This is the owner's list and nothing is on it until they say so -
    /// a shipped blocklist is a list somebody else maintains against a library they cannot
    /// see.
    /// </para>
    /// </summary>
    public List<string> ExcludeTerms { get; set; } = [];

    public List<PrivateTrackerSettings> PrivateTrackers { get; set; } = [];

    /// <summary>
    /// Trackers added to a magnet this plugin had to build itself.
    ///
    /// <para>
    /// A site that lists a torrent file rather than a magnet gives an info hash and no
    /// swarm. DHT alone is not enough: on a real server it asked for five minutes and
    /// nobody answered, every cycle, and nothing downloaded for a fortnight because of it.
    /// </para>
    ///
    /// <para>
    /// Ordinary public trackers, and a setting rather than a constant for two reasons -
    /// which ones work changes over time, and an owner is entitled to see and change what
    /// their server talks to. Emptied deliberately means DHT only, which is a choice
    /// somebody may want and this will not override.
    /// </para>
    ///
    /// <para>
    /// Never used for a magnet a site published. That one already names the swarm its own
    /// users are in.
    /// </para>
    /// </summary>
    public List<string> DefaultTrackers { get; set; } =
    [
        "udp://tracker.opentrackr.org:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://explodie.org:6969/announce",
    ];

    // Shows to follow that have no episode on the server yet. The plugin otherwise only
    // finishes what somebody already started by hand, which is a coherent tool and not
    // the one anybody wants - see the "monitored" section of the design spec.
    //
    // Ids rather than titles: a title is the thing the owner renames, and the library
    // keys on the id everywhere else in this plugin.
    public List<int> FollowedShowIds { get; set; } = [];

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

