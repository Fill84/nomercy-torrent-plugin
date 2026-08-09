// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Globalization;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

// The dashboard's only way to configure this plugin: no env vars, no config-file editing.
// Build is pure - settings and grant results in, a PluginView out, no I/O and no
// IPluginContext - so all ten SettingsViewTests are cheap in-memory assertions. GetViewAsync
// on TorrentDownloaderPlugin is where the loading, grant-checking and secret-key lookup
// belong; this method never sees a secret's actual value, only whether one is stored, which
// is what makes it structurally incapable of echoing one back to the client.
public static class SettingsView
{
    // Internal rather than private so TorrentDownloaderSettingsController's [HttpPost] route
    // attributes and this view's CallPlugin calls read the same literals instead of copies
    // that could drift apart - the host combines a controller's route with the plugin's own
    // prefix, so these strings are what make CallPlugin(...) actually resolve to something.
    //
    // The client interpolates CallPlugin's method straight into the request path
    // (plugins/{pluginId}/{method}) and posts the form's own fields as the body, discarding
    // anything else the action intent carried - a PluginForm's submit never forwards the
    // intent's payload. So a per-entry form's identity cannot ride in the payload (that was
    // the defect); it rides in the method string instead, as "SaveIndexer/{index}", which is
    // why these are shared route stems rather than full method names.
    internal const string SaveSettingsMethod = "SaveSettings";
    internal const string SaveIndexerMethod = "SaveIndexer";
    internal const string SavePrivateTrackerMethod = "SavePrivateTracker";

    // Add carries no index - it targets no existing entry, so there is nothing for the
    // route to parameterise. Remove needs one, for the same reason the save routes do:
    // the client's PluginButton dispatches its action's payload intact (unlike a
    // form), but the index still rides in the method string for consistency with the save
    // routes above, rather than one entry point reading the index from the route and its
    // sibling reading it from the payload.
    internal const string AddIndexerMethod = "AddIndexer";
    internal const string AddPrivateTrackerMethod = "AddPrivateTracker";
    internal const string RemoveIndexerMethod = "RemoveIndexer";
    internal const string RemovePrivateTrackerMethod = "RemovePrivateTracker";

    // Route templates the controller attaches its [HttpPost] to. Built from the same method
    // constants above at compile time, so the "{method}/{index}" shape used when building a
    // per-entry action and the "{method}/{index:int}" route the controller listens on cannot
    // drift into two different stems.
    internal const string SaveIndexerRouteTemplate = SaveIndexerMethod + "/{index:int}";
    internal const string SavePrivateTrackerRouteTemplate = SavePrivateTrackerMethod + "/{index:int}";
    internal const string RemoveIndexerRouteTemplate = RemoveIndexerMethod + "/{index:int}";
    internal const string RemovePrivateTrackerRouteTemplate = RemovePrivateTrackerMethod + "/{index:int}";

    private const string SaveLabel = "Save";

    public static PluginView Build(
        TorrentDownloaderSettings settings,
        IReadOnlyList<string> ungrantedHosts,
        IReadOnlySet<string> storedSecretKeys
    )
    {
        List<PluginComponent> children =
        [
            Ui.Text("settings-heading", "Torrent Downloader Settings", "title"),
            Ui.Text("settings-last-saved", LastSavedLabel(settings.LastSavedAtUtc), "caption"),
        ];

        if (ungrantedHosts.Count > 0)
        {
            children.Add(BuildGrantWarning(ungrantedHosts));
        }

        children.Add(BuildGeneralForm(settings));

        children.Add(Ui.Text("settings-indexers-heading", "Indexers", "subtitle"));
        children.Add(Ui.Button("settings-indexers-add", "Add indexer", PluginActionIntent.CallPlugin(AddIndexerMethod)));
        if (settings.Indexers.Count > 0)
        {
            for (int i = 0; i < settings.Indexers.Count; i++)
            {
                children.Add(BuildIndexerForm(i, settings.Indexers[i], storedSecretKeys));
                children.Add(BuildRemoveIndexerButton(i));
            }
        }
        else
        {
            children.Add(
                Ui.EmptyState(
                    "settings-indexers-empty",
                    "No indexer configured",
                    "Add an indexer so Torrent Downloader knows where to search for releases."
                )
            );
        }

        children.Add(Ui.Text("settings-trackers-heading", "Private Trackers", "subtitle"));
        children.Add(
            Ui.Text(
                "settings-trackers-explainer",
                "Everything else is public and never uploads. A tracker added here is the only way this plugin seeds.",
                "caption"
            )
        );
        children.Add(Ui.Button("settings-trackers-add", "Add private tracker", PluginActionIntent.CallPlugin(AddPrivateTrackerMethod)));
        if (settings.PrivateTrackers.Count > 0)
        {
            for (int i = 0; i < settings.PrivateTrackers.Count; i++)
            {
                children.Add(BuildPrivateTrackerForm(i, settings.PrivateTrackers[i], storedSecretKeys));
                children.Add(BuildRemovePrivateTrackerButton(i));
            }
        }
        else
        {
            children.Add(
                Ui.EmptyState(
                    "settings-trackers-empty",
                    "No private tracker configured",
                    "Without one, every torrent is treated as public: nothing is ever uploaded."
                )
            );
        }

        return PluginViews.Declarative(0, Ui.Container("settings-root", [.. children]));
    }

    // Rendered with CultureInfo.InvariantCulture, same as Core's size formatting, so this
    // does not shift shape by server locale - a comma-for-decimal locale reading the clock
    // differently would otherwise make the same instant look like two different formats to
    // two owners on two servers. The client discards a successful action's response body
    // entirely and re-fetches the view itself afterward, so this line - not the response -
    // is what tells the owner a save actually reached disk.
    private static string LastSavedLabel(DateTimeOffset? lastSavedAtUtc) =>
        lastSavedAtUtc is { } savedAt
            ? $"Last saved {savedAt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC"
            : "Not saved yet.";

    // Ids live outside the "settings-indexer-"/"settings-tracker-" namespace the per-entry
    // form uses (settings-indexer-{index}-form) - a component here landing inside that
    // namespace would collide with tests that gather every component by that prefix and
    // then treat every match as a form.
    private static PluginComponent BuildRemoveIndexerButton(int index) =>
        Ui.DestructiveButton(
            $"indexer-{index}-remove",
            "Remove indexer",
            $"{RemoveIndexerMethod}/{index}",
            "Remove this indexer?",
            "This deletes the indexer and its saved API key. This cannot be undone."
        );

    // A badge plus the sentence naming the hosts, so the owner knows a grant prompt they
    // may have missed is why nothing is downloading yet.
    private static PluginComponent BuildGrantWarning(IReadOnlyList<string> ungrantedHosts)
    {
        return Ui.Container(
            "settings-grant-warning",
            Ui.Badge("settings-grant-warning-badge", "Access needed", PluginBadgeVariant.Warning),
            Ui.Text(
                "settings-grant-warning-text",
                $"Torrent Downloader is waiting on host access for: {string.Join(", ", ungrantedHosts)}.",
                "body"
            )
        );
    }

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

        return Ui.Form("settings-general-form", SaveLabel, PluginActionIntent.CallPlugin(SaveSettingsMethod), fields);
    }

    private static PluginComponent BuildIndexerForm(int index, IndexerSettings indexer, IReadOnlySet<string> storedSecretKeys)
    {
        bool hasStoredKey = storedSecretKeys.Contains(SettingsGateway.IndexerSecretKey(indexer.Name));

        PluginFormField[] fields =
        [
            new() { Name = "name", Label = "Name", Value = indexer.Name, Required = true },
            new() { Name = "kind", Label = "Kind", Value = indexer.Kind },
            new() { Name = "url", Label = "URL", Value = indexer.Url, Required = true },
            new() { Name = "priority", Label = "Priority", Type = PluginFormFieldType.Number, Value = indexer.Priority },
            new() { Name = "enabled", Label = "Enabled", Type = PluginFormFieldType.Toggle, Value = indexer.Enabled },
            new()
            {
                Name = "minimumIntervalSeconds",
                Label = "Minimum interval (seconds)",
                Type = PluginFormFieldType.Number,
                Value = indexer.MinimumIntervalSeconds,
            },
            new() { Name = "categories", Label = "Categories", Value = string.Join(", ", indexer.Categories) },
            BuildSecretField("apiKey", "API key", hasStoredKey),
        ];

        return Ui.Form(
            $"settings-indexer-{index}-form",
            SaveLabel,
            PluginActionIntent.CallPlugin($"{SaveIndexerMethod}/{index}"),
            fields
        );
    }

    // The announce URL is a secret field, not a URL field, and that is the whole shape of
    // this form. Everything else about a private tracker is ordinary configuration; the
    // one thing that identifies the account never comes back down the wire.
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
            PluginActionIntent.CallPlugin($"{SavePrivateTrackerMethod}/{index}"),
            fields
        );
    }

    private static PluginComponent BuildRemovePrivateTrackerButton(int index) =>
        Ui.DestructiveButton(
            $"tracker-{index}-remove",
            "Remove private tracker",
            $"{RemovePrivateTrackerMethod}/{index}",
            "Remove this private tracker?",
            "This deletes the tracker, its announce URL and its API key. Torrents from it become public, and stop seeding."
        );

    // Never carries the stored value - Build never receives it in the first place. The
    // placeholder is the only signal of "stored", so the owner can tell "never set" from
    // "set, just not shown" instead of an empty box reading as a lost key either way.
    private static PluginFormField BuildSecretField(string name, string label, bool isStored)
    {
        return new PluginFormField
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
}
