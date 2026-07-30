// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// What one of the three forms actually posts. Which entry (if any) this submission is
// about is no longer carried in here - a PluginForm's submit discards whatever payload
// the action intent was built with, so an indexer/client identity field placed on this
// request would never arrive (that was the defect). The entry's identity now lives in
// the URL instead - SaveIndexer/{index} and SaveClient/{index} - which is the one part
// of the request a form's submit cannot strip, so SettingsSaveHandler's per-entry
// methods take it as a parameter rather than reading it off this type.
//
// Nullable throughout rather than defaulted, because a missing indexer/client field
// still needs to read as "not supplied" so the merge can fall back to the entry's
// current value - only the general form's own fields are always present together.
public sealed class SaveSettingsRequest
{
    // General form.
    public string? TransfersCron { get; init; }
    public string? FeedCron { get; init; }
    public string? SearchCron { get; init; }
    public string? MaintenanceCron { get; init; }
    public string? IncompleteFolder { get; init; }
    public string? IntakeFolder { get; init; }

    // Shared by the indexer and client forms - never both submitted in the same
    // request, since SettingsView renders one form per entry.
    public string? Name { get; init; }
    public string? Kind { get; init; }
    public string? Url { get; init; }
    public bool? Enabled { get; init; }

    // Indexer-only.
    public int? Priority { get; init; }
    public int? MinimumIntervalSeconds { get; init; }
    public string? Categories { get; init; }
    public string? ApiKey { get; init; }

    // Client-only.
    public string? Username { get; init; }
    public string? Password { get; init; }
}
