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

    public List<IndexerSettings> Indexers { get; set; } = [];
    public List<TorrentClientSettings> Clients { get; set; } = [];

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

// No Password here, for the same reason.
public class TorrentClientSettings
{
    public string Name { get; set; } = string.Empty;
    public string Kind { get; set; } = "qbittorrent";
    public string Url { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
}
