// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// How the plugin is set up: folders, schedules, what quality to accept, and the private
/// trackers it is allowed to seed to.
///
/// <para>
/// Sources used to be here too and are now their own page - they are the thing an owner adds
/// and watches, not the thing they set once. What is left is the settings sense of settings.
/// </para>
///
/// <para>
/// Build is pure - settings in, a PluginView out, no I/O and no IPluginContext - so its
/// tests are cheap in-memory assertions. <c>GetViewAsync</c> is where the loading and the
/// secret-key lookup belong; this method never sees a secret's actual value, only whether
/// one is stored, which is what makes it structurally incapable of echoing one back.
/// </para>
/// </summary>
public static class SettingsView
{
    private const string SaveLabel = "Save";

    public static PluginView Build(TorrentDownloaderSettings settings, IReadOnlySet<string> storedSecretKeys)
    {
        List<PluginComponent> children =
        [
            // The client discards a successful action's response body entirely and re-fetches
            // the view itself afterwards, so this line - not the response - is what tells the
            // owner a save actually reached disk.
            Ui.Text("settings-last-saved", LastSavedLabel(settings.LastSavedAtUtc), "caption"),
            BuildGeneralForm(settings),
            Ui.Text("settings-trackers-heading", Format.Count("Private trackers", settings.PrivateTrackers.Count), "subtitle"),
            Ui.Text(
                "settings-trackers-explainer",
                "Everything else is public and never uploads. A tracker added here is the only way this plugin seeds.",
                "caption"),
            Ui.Button("settings-trackers-add", "Add private tracker", PluginActionIntent.CallPlugin(PluginMethods.AddPrivateTracker)),
        ];

        if (settings.PrivateTrackers.Count > 0)
        {
            for (int index = 0; index < settings.PrivateTrackers.Count; index++)
            {
                // One block per tracker, so its form and the button that deletes it stay
                // together rather than the remove button sitting above the next one's name.
                children.Add(Ui.Container(
                    $"settings-tracker-{index}",
                    BuildPrivateTrackerForm(index, settings.PrivateTrackers[index], storedSecretKeys),
                    BuildRemovePrivateTrackerButton(index)));
            }
        }
        else
        {
            children.Add(Ui.EmptyState(
                "settings-trackers-empty",
                "No private tracker configured",
                "Without one, every torrent is treated as public: nothing is ever uploaded."));
        }

        // Zero: nothing on this page changes on its own, and a form that re-renders under
        // the owner's fingers loses what they were typing.
        return Pages.Page(Pages.Settings, 0, [.. children]);
    }

    // Rendered with CultureInfo.InvariantCulture, same as Core's size formatting, so this
    // does not shift shape by server locale - a comma-for-decimal locale reading the clock
    // differently would otherwise make the same instant look like two different formats to
    // two owners on two servers.
    private static string LastSavedLabel(DateTimeOffset? lastSavedAtUtc) =>
        lastSavedAtUtc is { } savedAt
            ? $"Last saved {savedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
            : "Not saved yet.";

    private static PluginComponent BuildGeneralForm(TorrentDownloaderSettings settings)
    {
        PluginFormField[] fields =
        [
            new() { Name = "transfersCron", Label = "Transfers schedule", Value = settings.TransfersCron, Required = true },
            new() { Name = "feedCron", Label = "Feed schedule", Value = settings.FeedCron, Required = true },
            new() { Name = "searchCron", Label = "Search schedule", Value = settings.SearchCron, Required = true },
            new() { Name = "maintenanceCron", Label = "Maintenance schedule", Value = settings.MaintenanceCron, Required = true },
            new() { Name = "incompleteFolder", Label = "Incomplete downloads folder", Value = settings.IncompleteFolder },
            new() { Name = "intakeFolder", Label = "Intake folder", Value = settings.IntakeFolder },
            new()
            {
                Name = "includeSpecials",
                Label = "Download specials (season 0)",
                Type = PluginFormFieldType.Toggle,
                Value = settings.IncludeSpecials,
            },
            new()
            {
                Name = "maximumResolution",
                Label = "Highest quality to download",
                Type = PluginFormFieldType.Select,
                Value = settings.MaximumResolution,
                Options =
                [
                    new PluginFormOption { Value = "720p", Label = "Up to 720p" },
                    new PluginFormOption { Value = "1080p", Label = "Up to 1080p" },
                    new PluginFormOption { Value = "2160p", Label = "Up to 2160p" },
                ],
            },
            new()
            {
                Name = "minimumSeeders",
                Label = "Minimum seeders",
                Type = PluginFormFieldType.Number,
                Value = settings.MinimumSeeders,
            },
            new()
            {
                Name = "allowSeasonPacks",
                Label = "Allow season packs",
                Type = PluginFormFieldType.Toggle,
                Value = settings.AllowSeasonPacks,
            },
        ];

        return Ui.Form("settings-general-form", SaveLabel, PluginActionIntent.CallPlugin(PluginMethods.SaveSettings), fields);
    }

    // The announce URL is a secret field, not a URL field, and that is the whole shape of
    // this form. Everything else about a private tracker is ordinary configuration; the one
    // thing that identifies the account never comes back down the wire.
    private static PluginComponent BuildPrivateTrackerForm(int index, PrivateTrackerSettings tracker, IReadOnlySet<string> storedSecretKeys)
    {
        bool hasStoredAnnounce = storedSecretKeys.Contains(SettingsGateway.PrivateTrackerAnnounceKey(tracker.Name));
        bool hasStoredApiKey = storedSecretKeys.Contains(SettingsGateway.PrivateTrackerApiKeyKey(tracker.Name));

        PluginFormField[] fields =
        [
            new() { Name = "name", Label = "Name", Value = tracker.Name, Required = true },
            BuildSecretField("announceUrl", "Announce URL", hasStoredAnnounce),
            BuildSecretField("apiKey", "API key", hasStoredApiKey),
            new() { Name = "enabled", Label = "Enabled", Type = PluginFormFieldType.Toggle, Value = tracker.Enabled },
            new() { Name = "seed", Label = "Seed to this tracker", Type = PluginFormFieldType.Toggle, Value = tracker.Seed },
            new()
            {
                Name = "seedRatioTarget",
                Label = "Seed until ratio",
                Type = PluginFormFieldType.Number,
                Value = tracker.SeedRatioTarget,
            },
            new()
            {
                Name = "seedTimeTargetHours",
                Label = "Seed for at least (hours)",
                Type = PluginFormFieldType.Number,
                Value = tracker.SeedTimeTargetHours,
            },
        ];

        return Ui.Form(
            $"settings-tracker-{index}-form",
            SaveLabel,
            PluginActionIntent.CallPlugin($"{PluginMethods.SavePrivateTracker}/{index}"),
            fields);
    }

    private static PluginComponent BuildRemovePrivateTrackerButton(int index) =>
        Ui.DestructiveButton(
            $"tracker-{index}-remove",
            "Remove private tracker",
            $"{PluginMethods.RemovePrivateTracker}/{index}",
            "Remove this private tracker?",
            "This deletes the tracker, its announce URL and its API key. Torrents from it become public, and stop seeding.");

    // Never carries the stored value - Build never receives it in the first place. The
    // placeholder is the only signal of "stored", so the owner can tell "never set" from
    // "set, just not shown" instead of an empty box reading as a lost key either way.
    private static PluginFormField BuildSecretField(string name, string label, bool isStored) =>
        new()
        {
            Name = name,
            Label = label,
            Type = PluginFormFieldType.Password,
            Value = null,
            Placeholder = isStored
                ? $"{label} is already saved. Leave blank to keep it."
                : $"No {label.ToLowerInvariant()} saved yet.",
        };
}
