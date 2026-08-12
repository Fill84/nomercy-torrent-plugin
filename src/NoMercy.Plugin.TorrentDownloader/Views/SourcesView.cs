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
        IReadOnlyList<HistoryEntry> history,
        IReadOnlyList<SourceReport>? reports = null)
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
            "A feed announces releases by name. A site is searched for that name and has the torrents.",
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
                    Label = "Address",

                    // The placeholder does the teaching, because it shows the shape rather
                    // than describing it. A label reading "put {query} where the terms go"
                    // was on screen while an address was pasted without it - people copy an
                    // example, they do not parse a sentence.
                    Placeholder = $"https://a-site.test/search/{SiteIndexer.QueryPlaceholder}",
                    Required = true,
                })));

        children.Add(Ui.Section(
            "sources-configured",
            Format.Count("Sources", settings.Indexers.Count),
            settings.Indexers.Count == 0 ? null : "Click one to change it.",
            List(settings, history, reports)));

        return Pages.Page(Pages.Sources, 0, [.. children]);
    }

    /// <summary>
    /// One page for the whole configuration, not one form per source stacked on top of each
    /// other.
    ///
    /// <para>
    /// Three sources meant three eight-field forms open at once - twenty-four boxes down one
    /// page, each with a full-width red delete bar between it and the next source's name.
    /// The list answers what somebody comes here for (which sources exist, are they on, are
    /// they producing anything) and the form for one of them is one click away.
    /// </para>
    /// </summary>
    private static PluginComponent List(
        TorrentDownloaderSettings settings,
        IReadOnlyList<HistoryEntry> history,
        IReadOnlyList<SourceReport>? reports)
    {
        if (settings.Indexers.Count == 0)
        {
            return Ui.EmptyState(
                "sources-empty",
                "No source configured",
                "Nothing is searched until there is one.");
        }

        List<PluginComponent> rows = [];

        for (int index = 0; index < settings.Indexers.Count; index++)
        {
            IndexerSettings indexer = settings.Indexers[index];

            rows.Add(Ui.TableRow(
                $"sources-row-{index}",
                new()
                {
                    ["kind"] = indexer.Enabled ? KindLabel(indexer.Kind) : "Off",
                    ["kindVariant"] = indexer.Enabled ? PluginBadgeVariant.Info : PluginBadgeVariant.Neutral,
                    ["name"] = indexer.Name,
                    ["address"] = indexer.Url,
                    ["answered"] = LastAnswer(indexer, reports),
                    ["yielded"] = Yield(indexer, history),
                },
                Pages.Routes.GoTo(Pages.Source, new Dictionary<string, string> { ["index"] = index.ToString() })));
        }

        return Ui.Table("sources-list", ListColumns, rows);
    }

    /// <summary>
    /// What this source said the last time it was asked, which is not the same question as
    /// what it has produced.
    ///
    /// <para>
    /// Yield counts grabs, so a source returning forty releases the profile turns down and a
    /// source answering 403 behind a Cloudflare check both read as a blank. On a real server
    /// two of three sources were the second for weeks, and nothing on any page said so - the
    /// only way to find out was to ask the site by hand.
    /// </para>
    /// </summary>
    private static string LastAnswer(IndexerSettings indexer, IReadOnlyList<SourceReport>? reports)
    {
        SourceReport? report = reports?.FirstOrDefault(entry =>
            string.Equals(entry.Name, indexer.Name, StringComparison.OrdinalIgnoreCase));

        if (report is null)
            return "not asked yet";

        string when = Format.Ago(report.At);

        // The reason verbatim and truncated rather than summarised: "a Cloudflare check",
        // "no API key" and "timed out" need different things done about them, and a page
        // that flattens them into "failed" sends the owner back to the log.
        return report.Failure is { Length: > 0 } failure
            ? $"{Trimmed(failure)} - {when}"
            : $"{report.Released} release(s) - {when}";
    }

    private static string Trimmed(string reason) =>
        reason.Length <= 70 ? reason : reason[..69] + "…";

    private static readonly PluginTableColumn[] ListColumns =
    [
        new() { Key = "kind", Label = "Kind", Cell = PluginTableCellType.Badge, Width = "6rem" },
        new() { Key = "name", Label = "Name", Width = "12rem" },
        new() { Key = "address", Label = "Address" },
        new() { Key = "answered", Label = "Last answer", Width = "16rem" },
        new() { Key = "yielded", Label = "Yielded", Width = "12rem" },
    ];

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
    private static string Yield(IndexerSettings indexer, IReadOnlyList<HistoryEntry> history)
    {
        List<HistoryEntry> mine =
        [
            .. history.Where(entry => string.Equals(entry.Indexer, indexer.Name, StringComparison.OrdinalIgnoreCase)),
        ];

        if (mine.Count == 0)
            return "Nothing yet";

        int imported = mine.Count(entry => entry.Event == HistoryEvent.Imported);

        return $"{imported} imported, last {Format.Ago(mine.Max(entry => entry.At))}";
    }

    /// <summary>One source, with room for its form and nothing else competing for the page.</summary>
    public static PluginView Detail(
        int index,
        IndexerSettings indexer,
        IReadOnlySet<string> storedSecretKeys,
        IReadOnlyList<HistoryEntry> history) =>
        Pages.Page(
            Pages.Source,
            indexer.Name,
            0,
            Ui.Row(
                "source-state",
                Ui.Badge(
                    "source-kind",
                    indexer.Enabled ? KindLabel(indexer.Kind) : "Off",
                    indexer.Enabled ? PluginBadgeVariant.Info : PluginBadgeVariant.Neutral),
                Ui.Text("source-yielded", Yield(indexer, history))),

            // In a section, like every other page's content. It was a bare form under a
            // badge, which is the shape the settings page had and the reason that one read
            // as a different plugin.
            Ui.Section(
                "source-settings",
                "How it is asked",
                "The address is used as it is written. A site needs {query} where the search terms go.",
                IndexerForm(index, indexer, storedSecretKeys)),

            // In a row, not loose in the column. A button that is a direct child of a column
            // is stretched to the width of the page, which turned a delete into a red bar
            // across the whole screen.
            Ui.Row("source-actions", RemoveButton(index)),
            Ui.Row("source-back", Ui.Button("source-back-button", "Back to sources", Pages.Routes.GoTo(Pages.Sources))));

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
            new()
            {
                Name = "url",
                Label = UrlLabel(indexer.Kind),
                Value = indexer.Url,
                Placeholder = UrlPlaceholder(indexer.Kind),
                Required = true,
            },
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

    private static string? UrlPlaceholder(string kind) =>
        kind.Equals("site", StringComparison.OrdinalIgnoreCase)
            ? $"https://a-site.test/search/{SiteIndexer.QueryPlaceholder}"
            : null;

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
