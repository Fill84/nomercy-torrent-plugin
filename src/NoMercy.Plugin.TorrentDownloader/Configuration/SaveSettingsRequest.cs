// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// What SaveSettings actually receives: one of three forms, distinguished by which
// identity field is present. IndexerName/ClientName is never a field the owner
// edits - it is the action payload SettingsView attaches to a form so the handler
// knows which existing entry (if any) this submission is about, separate from the
// "name" field the owner CAN edit and which may therefore differ from it (a rename).
//
// Nullable throughout rather than defaulted, because a missing indexer/client field
// still needs to read as "not supplied" so the merge can fall back to the entry's
// current value - only the general form's own fields are always present together.
public sealed class SaveSettingsRequest
{
    public string? IndexerName { get; init; }
    public string? ClientName { get; init; }

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
