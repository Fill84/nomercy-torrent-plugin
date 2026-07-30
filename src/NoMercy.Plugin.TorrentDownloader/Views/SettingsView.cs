// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

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
    private const string SaveSettingsMethod = "SaveSettings";

    // Saving needs a REST endpoint: PluginActionIntent.CallPlugin dispatches over the
    // transport named in its payload, which defaults to rest, and this version declares
    // "rest": false with no controller behind it. So the submit action is declared but
    // goes nowhere, and PluginViews.Form has no overload that omits one.
    //
    // Rather than let a user type an API key into a password box and press a button that
    // silently does nothing - leaving them to believe the credential was stored - the
    // page says so up front and the button says so too. Both go away with the REST
    // surface, along with this comment.
    private const string SaveUnavailableLabel = "Saving is not available in this version";

    public static PluginView Build(
        TorrentDownloaderSettings settings,
        IReadOnlyList<string> ungrantedHosts,
        IReadOnlySet<string> storedSecretKeys
    )
    {
        List<PluginComponent> children =
        [
            PluginViews.Text("settings-heading", "Torrent Downloader Settings", "heading"),
            PluginViews.Badge("settings-readonly-badge", "Read-only", PluginBadgeVariant.Warning),
            PluginViews.Text(
                "settings-readonly-notice",
                "This version can show its configuration but not change it — saving needs the "
                    + "plugin's REST surface, which is not built yet. Anything you type here will "
                    + "not be stored.",
                "body"
            ),
        ];

        if (ungrantedHosts.Count > 0)
        {
            children.Add(BuildGrantWarning(ungrantedHosts));
        }

        children.Add(BuildGeneralForm(settings));

        children.Add(PluginViews.Text("settings-indexers-heading", "Indexers", "subheading"));
        if (settings.Indexers.Count > 0)
        {
            for (int i = 0; i < settings.Indexers.Count; i++)
            {
                children.Add(BuildIndexerForm(i, settings.Indexers[i], storedSecretKeys));
            }
        }
        else
        {
            children.Add(
                PluginViews.EmptyState(
                    "settings-indexers-empty",
                    "No indexer configured",
                    "Add an indexer so Torrent Downloader knows where to search for releases."
                )
            );
        }

        children.Add(PluginViews.Text("settings-clients-heading", "Download Clients", "subheading"));
        if (settings.Clients.Count > 0)
        {
            for (int i = 0; i < settings.Clients.Count; i++)
            {
                children.Add(BuildClientForm(i, settings.Clients[i], storedSecretKeys));
            }
        }
        else
        {
            children.Add(
                PluginViews.EmptyState(
                    "settings-clients-empty",
                    "No download client configured",
                    "Add a torrent client so Torrent Downloader has somewhere to send what it finds."
                )
            );
        }

        return PluginViews.Declarative(0, PluginViews.Container("settings-root", [.. children]));
    }

    // A badge plus the sentence naming the hosts, so the owner knows a grant prompt they
    // may have missed is why nothing is downloading yet.
    private static PluginComponent BuildGrantWarning(IReadOnlyList<string> ungrantedHosts)
    {
        return PluginViews.Container(
            "settings-grant-warning",
            PluginViews.Badge("settings-grant-warning-badge", "Access needed", PluginBadgeVariant.Warning),
            PluginViews.Text(
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
        ];

        return PluginViews.Form("settings-general-form", SaveUnavailableLabel, PluginActionIntent.CallPlugin(SaveSettingsMethod), fields);
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

        return PluginViews.Form(
            $"settings-indexer-{index}-form",
            SaveUnavailableLabel,
            PluginActionIntent.CallPlugin(SaveSettingsMethod, new Dictionary<string, object?> { ["indexerName"] = indexer.Name }),
            fields
        );
    }

    private static PluginComponent BuildClientForm(int index, TorrentClientSettings client, IReadOnlySet<string> storedSecretKeys)
    {
        bool hasStoredPassword = storedSecretKeys.Contains(SettingsGateway.ClientSecretKey(client.Name));

        PluginFormField[] fields =
        [
            new() { Name = "name", Label = "Name", Value = client.Name, Required = true },
            new() { Name = "kind", Label = "Kind", Value = client.Kind },
            new() { Name = "url", Label = "URL", Value = client.Url, Required = true },
            new() { Name = "username", Label = "Username", Value = client.Username },
            new() { Name = "enabled", Label = "Enabled", Type = PluginFormFieldType.Toggle, Value = client.Enabled },
            BuildSecretField("password", "Password", hasStoredPassword),
        ];

        return PluginViews.Form(
            $"settings-client-{index}-form",
            SaveUnavailableLabel,
            PluginActionIntent.CallPlugin(SaveSettingsMethod, new Dictionary<string, object?> { ["clientName"] = client.Name }),
            fields
        );
    }

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
