// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Configuration;

// What one of the three forms actually posts. Which entry (if any) this submission is
// about is no longer carried in here - a PluginForm's submit discards whatever payload
// the action intent was built with, so a per-entry identity field placed on this
// request would never arrive (that was the defect). The entry's identity now lives in
// the URL instead - SaveIndexer/{index}, SavePrivateTracker/{index} - which is the one part
// of the request a form's submit cannot strip, so SettingsSaveHandler's per-entry
// methods take it as a parameter rather than reading it off this type.
//
// Nullable throughout rather than defaulted, because a missing per-entry field
// still needs to read as "not supplied" so the merge can fall back to the entry's
// current value - only the general form's own fields are always present together.
public sealed class SaveSettingsRequest
{
    // General form.
    public string? TransfersCron { get; init; }
    public string? FeedCron { get; init; }

    /// <summary>A magnet link somebody pasted on the downloads page.</summary>
    public string? Source { get; init; }
    public string? SearchCron { get; init; }
    public string? MaintenanceCron { get; init; }
    public string? IncompleteFolder { get; init; }
    public string? IntakeFolder { get; init; }
    public bool? IncludeSpecials { get; init; }
    public string? MaximumResolution { get; init; }
    public int? MinimumSeeders { get; init; }
    public int? MaxConcurrentDownloads { get; init; }
    public bool? AllowSeasonPacks { get; init; }

    /// <summary>Comma separated, because a form field is one line and a tracker list is short.</summary>
    public string? DefaultTrackers { get; init; }

    public bool? UseBrowserForChallenges { get; init; }

    /// <summary>Where FlareSolverr is, or empty when the owner runs none.</summary>
    public string? FlareSolverrUrl { get; init; }

    /// <summary>h264, h265 or any.</summary>
    public string? Codec { get; init; }

    public bool? EnglishOnly { get; init; }

    /// <summary>Comma separated, like the tracker list, and for the same reason.</summary>
    public string? ExcludeTerms { get; init; }

    // Shared by the indexer and private tracker forms - never both submitted in the same
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


    // Private-tracker-only. AnnounceUrl is nullable for the same reason ApiKey is: the
    // form cannot render a stored secret back, so it submits blank whenever the owner is
    // editing something else on the same entry, and blank has to mean "leave it alone".
    public string? AnnounceUrl { get; init; }
    public bool? Seed { get; init; }
    public double? SeedRatioTarget { get; init; }
    public int? SeedTimeTargetHours { get; init; }
}
