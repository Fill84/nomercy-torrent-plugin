// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Where releases come from: feeds and sites together, with what each has actually yielded.
///
/// <para>
/// Split out of the settings page, which had them under "Indexers" beside the folders and
/// the schedules. They are not configuration in the same sense: a folder is set once, and a
/// source is the thing an owner adds, watches, and removes when it stops working. Putting
/// them on their own page is what makes "which of these is doing anything" a question the
/// page can answer.
/// </para>
///
/// <para>
/// Declares no refresh. Every other page's numbers move on their own; this one is mostly
/// forms, and a form that re-renders under the owner's fingers loses what they were typing.
/// </para>
/// </summary>
public static class SourcesView
{
    private const string SaveLabel = "Save";

    /// <summary>
    /// How far back the yield line counts.
    ///
    /// <para>
    /// Deeper than any page that lists history, because this reads it rather than showing
    /// it: "nothing from this one yet" is a claim about the source, and a shallow window
    /// would make a feed that worked all last month look dead.
    /// </para>
    /// </summary>
    public const int HistoryDepth = 500;

    public static PluginView Build(
        TorrentDownloaderSettings settings,
        IReadOnlyList<string> ungrantedHosts,
        IReadOnlySet<string> storedSecretKeys,
        IReadOnlyList<HistoryEntry> history)
    {
        List<PluginComponent> children = [];

        if (ungrantedHosts.Count > 0)
        {
            children.Add(GrantWarning(ungrantedHosts));
        }

        // The one-shot form first: adding a source is the thing an owner comes here to do,
        // and it is complete in one go - name, kind and address together - rather than
        // appending a blank entry they then have to find and fill in.
        children.Add(Ui.Section(
            "sources-add",
            "Add a source",
            "A feed announces which releases exist; a site is where the torrent is, searched by that name. Most setups want one of each.",
            Ui.Form(
                "sources-add-form",
                "Add source",
                PluginActionIntent.CallPlugin(PluginMethods.AddSource),
                new PluginFormField { Name = "name", Label = "Name", Required = true },
                new PluginFormField
                {
                    Name = "kind",
                    Label = "Kind",
                    Type = PluginFormFieldType.Select,
                    Value = "rss",
                    Options = [.. Kinds()],
                },
                new PluginFormField
                {
                    Name = "url",
                    Label = $"Address - a site needs {SiteIndexer.QueryPlaceholder} where the search terms go",
                    Required = true,
                })));

        children.Add(Ui.Text("sources-configured-heading", Format.Count("Sources", settings.Indexers.Count), "subtitle"));

        if (settings.Indexers.Count == 0)
        {
            children.Add(Ui.EmptyState(
                "sources-empty",
                "No source configured",
                "Nothing is searched and nothing is announced until there is at least one."));
        }
        else
        {
            for (int index = 0; index < settings.Indexers.Count; index++)
            {
                IndexerSettings indexer = settings.Indexers[index];

                // One block per source, so the yield line, the form it describes and the
                // button that deletes it read as one thing. Loose siblings put a remove
                // button directly above the next source's name, which is a bad place for it.
                children.Add(Ui.Container(
                    $"sources-{index}",
                    Yield(index, indexer, history),
                    IndexerForm(index, indexer, storedSecretKeys),
                    RemoveButton(index)));
            }
        }

        return Pages.Page(Pages.Sources, 0, [.. children]);
    }

    /// <summary>
    /// What this source has actually produced, which is the only honest way to tell a
    /// working feed from one whose URL has quietly started returning nothing.
    ///
    /// <para>
    /// Read off the history rather than counted somewhere of its own: history already
    /// records which source each release came from, and a second tally kept beside it would
    /// be one more thing to keep true.
    /// </para>
    /// </summary>
    private static PluginComponent Yield(int index, IndexerSettings indexer, IReadOnlyList<HistoryEntry> history)
    {
        List<HistoryEntry> mine =
        [
            .. history.Where(entry => string.Equals(entry.Indexer, indexer.Name, StringComparison.OrdinalIgnoreCase)),
        ];

        int imported = mine.Count(entry => entry.Event == HistoryEvent.Imported);

        string yielded = mine.Count == 0
            ? "Nothing from this one yet."
            : imported == 1
                ? "1 episode imported from this one."
                : $"{imported} episodes imported from this one.";

        string last = mine.Count == 0
            ? ""
            : $" Last heard from {Format.Ago(mine.Max(entry => entry.At))}.";

        return Ui.Row(
            $"sources-{index}-yield",
            Ui.Badge(
                $"sources-{index}-state",
                indexer.Enabled ? KindLabel(indexer.Kind) : "Disabled",
                indexer.Enabled ? PluginBadgeVariant.Info : PluginBadgeVariant.Neutral),
            Ui.Text($"sources-{index}-name", indexer.Name),
            Ui.Text($"sources-{index}-yielded", yielded + last, "caption"));
    }

    // A badge plus the sentence naming the hosts, so the owner knows a grant prompt they may
    // have missed is why nothing is downloading yet.
    private static PluginComponent GrantWarning(IReadOnlyList<string> ungrantedHosts) =>
        Ui.Row(
            "sources-grant-warning",
            Ui.Badge("sources-grant-warning-badge", "Access needed", PluginBadgeVariant.Warning),
            Ui.Text(
                "sources-grant-warning-text",
                $"Torrent Downloader is waiting on host access for: {string.Join(", ", ungrantedHosts)}."));

    private static PluginComponent IndexerForm(int index, IndexerSettings indexer, IReadOnlySet<string> storedSecretKeys)
    {
        bool hasStoredKey = storedSecretKeys.Contains(SettingsGateway.IndexerSecretKey(indexer.Name));

        PluginFormField[] fields =
        [
            new() { Name = "name", Label = "Name", Value = indexer.Name, Required = true },

            // A list rather than a text box. The three kinds do genuinely different jobs, and
            // typing one of them from memory is how a source ends up silently skipped for a
            // spelling nobody can see on the page.
            new()
            {
                Name = "kind",
                Label = "Kind",
                Type = PluginFormFieldType.Select,
                Value = indexer.Kind,
                Options = [.. Kinds()],
            },
            new() { Name = "url", Label = UrlLabel(indexer.Kind), Value = indexer.Url, Required = true },
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
            SecretField("apiKey", "API key", hasStoredKey),
        ];

        return Ui.Form(
            $"sources-indexer-{index}-form",
            SaveLabel,
            PluginActionIntent.CallPlugin($"{PluginMethods.SaveIndexer}/{index}"),
            fields);
    }

    private static PluginComponent RemoveButton(int index) =>
        Ui.DestructiveButton(
            $"sources-{index}-remove",
            "Remove source",
            $"{PluginMethods.RemoveIndexer}/{index}",
            "Remove this source?",
            "This deletes the source and its saved API key. This cannot be undone.");

    private static IEnumerable<PluginFormOption> Kinds() =>
    [
        new() { Value = "rss", Label = "Feed - announces releases by name" },
        new() { Value = "site", Label = "Site - searched for a release, has the torrents" },
        new() { Value = "torznab", Label = "Torznab - Jackett or Prowlarr" },
    ];

    private static string KindLabel(string kind) => kind.ToLowerInvariant() switch
    {
        "site" => "Site",
        "torznab" => "Torznab",
        _ => "Feed",
    };

    /// <summary>
    /// What to put in the URL box, which is not the same question for all three kinds.
    ///
    /// <para>
    /// A site needs its search address with a placeholder in it, and that is not guessable -
    /// so the label asks for exactly what the owner can copy out of their own address bar
    /// after searching the site once by hand.
    /// </para>
    /// </summary>
    private static string UrlLabel(string kind) =>
        kind.Equals("site", StringComparison.OrdinalIgnoreCase)
            ? $"Search address, with {SiteIndexer.QueryPlaceholder} where the search terms go"
            : "URL";

    // Never carries the stored value - Build never receives it in the first place. The
    // placeholder is the only signal of "stored", so the owner can tell "never set" from
    // "set, just not shown" instead of an empty box reading as a lost key either way.
    private static PluginFormField SecretField(string name, string label, bool isStored) =>
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
