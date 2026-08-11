// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

// The components the dashboard actually renders, named the way it names them.
//
// Taken from nomercy-radiostation-plugin's Ui.cs, which found this first and paid for it.
// The two plugins now send the same vocabulary; if the client ever moves to the
// design-system names, both change here and nowhere else.
//
// The contract this plugin compiles against maps every component onto a design-system
// name:
//
//     Container = List = Row = Grid = Card = Detail = Form = Table = "NMCard"
//
// The client keys its plugin components by their own names instead - "PluginForm",
// "PluginList", "PluginProgress" - and resolves a node in two steps: a design-system name
// is drawn as a design-system component, anything else is looked up in the plugin map. So
// every node sent as "NMCard" was drawn as a card, and the plugin component behind that
// name was never reached.
//
// What that cost here, observed on a real server rather than reasoned about:
//
//   - PluginForm is a real <form> that collects its fields and submits them. Sent as
//     "NMCard" it is a clickable box with no form in it, so every save arrived empty. An
//     indexer typed into the settings page was still "New Indexer 1" with a blank URL in
//     config.json afterwards, and the page said it had saved.
//   - PluginText reads `value`, not `text`, and knows the variants "title" and "subtitle".
//     Sent the other way it renders nothing at all.
//   - PluginList, PluginRow and PluginProgress lay their children out. Sent as "NMCard"
//     nothing did.
//
// The names and props below are read off the running client's own component map
// (src/types/plugins.ts, src/components/plugin/*.vue), not guessed.
public static class Ui
{
    public const string ContainerComponent = "PluginContainer";
    public const string TextComponent = "PluginText";
    public const string ListComponent = "PluginList";
    public const string RowComponent = "PluginRow";
    public const string ButtonComponent = "PluginButton";
    public const string FormComponent = "PluginForm";
    public const string EmptyStateComponent = "PluginEmptyState";
    public const string ProgressComponent = "PluginProgress";
    public const string BadgeComponent = "PluginBadge";
    public const string TableComponent = "PluginTable";
    public const string DetailComponent = "PluginDetail";

    // PluginCard and PluginGrid are drawn by the client and are deliberately not built here.
    // A card is ten rem wide and truncates its title, and this plugin's rows are show names
    // and release names - the two things that do not survive truncation. A card also exists
    // to carry artwork, and IPluginLibraryQuery hands a plugin no poster path. When it does,
    // the Shows page is where they belong.

    /// <summary>A column of children.</summary>
    public static PluginComponent Container(string id, params PluginComponent[] items) =>
        new() { Id = id, Component = ContainerComponent, Items = [.. items] };

    /// <inheritdoc cref="Container(string, PluginComponent[])"/>
    public static PluginComponent Container(string id, IEnumerable<PluginComponent> items) =>
        new() { Id = id, Component = ContainerComponent, Items = [.. items] };

    /// <summary>A vertical list, one child per row.</summary>
    public static PluginComponent List(string id, IEnumerable<PluginComponent> items) =>
        new() { Id = id, Component = ListComponent, Items = [.. items] };

    /// <summary>A wrapping row.</summary>
    public static PluginComponent Row(string id, params PluginComponent[] items) =>
        new() { Id = id, Component = RowComponent, Items = [.. items] };

    /// <inheritdoc cref="Row(string, PluginComponent[])"/>
    public static PluginComponent Row(string id, IEnumerable<PluginComponent> items) =>
        new() { Id = id, Component = RowComponent, Items = [.. items] };

    /// <summary>
    /// <c>value</c>, not <c>text</c>: PluginText reads its own prop name, and the variants
    /// it knows are "title" and "subtitle". Anything else reads as body text, which is the
    /// safe wrong answer rather than an empty node.
    /// </summary>
    public static PluginComponent Text(string id, string value, string? variant = null) =>
        new()
        {
            Id = id,
            Component = TextComponent,
            Props = new() { ["value"] = value, ["variant"] = variant },
        };

    public static PluginComponent Button(
        string id,
        string label,
        PluginActionIntent action,
        string? icon = null,
        string? variant = null) =>
        new()
        {
            Id = id,
            Component = ButtonComponent,
            Props = new() { ["label"] = label, ["icon"] = icon, ["variant"] = variant },
            Action = action,
        };

    /// <summary>
    /// A button that asks first. The confirmation rides on the action intent, not on the
    /// component, so the client shows the prompt and only then dispatches.
    /// </summary>
    public static PluginComponent DestructiveButton(
        string id,
        string label,
        string method,
        string confirmTitle,
        string? confirmMessage = null) =>
        Button(
            id,
            label,
            PluginActionIntent.CallPlugin(
                method,
                confirm: new PluginConfirmation
                {
                    Title = confirmTitle,
                    Message = confirmMessage,
                    ConfirmLabel = label,
                }),
            variant: "danger");

    /// <summary>
    /// A real form. Its fields are collected on submit and posted to the plugin, which is
    /// the whole reason this file exists.
    /// </summary>
    public static PluginComponent Form(
        string id,
        string submitLabel,
        PluginActionIntent action,
        params PluginFormField[] fields) =>
        new()
        {
            Id = id,
            Component = FormComponent,
            Props = new() { ["submitLabel"] = submitLabel, ["fields"] = fields },
            Action = action,
        };

    public static PluginComponent EmptyState(string id, string title, string? message = null) =>
        new()
        {
            Id = id,
            Component = EmptyStateComponent,
            Props = new() { ["title"] = title, ["message"] = message },
        };

    /// <summary>Zero to one. Null would draw an indeterminate bar; nothing here wants one.</summary>
    public static PluginComponent Progress(string id, double value, string? label = null) =>
        new()
        {
            Id = id,
            Component = ProgressComponent,
            Props = new() { ["value"] = value, ["label"] = label },
        };

    /// <summary>
    /// A titled block of the page: heading, an optional line saying what is in it, then the
    /// body.
    ///
    /// <para>
    /// The count belongs in the heading rather than left to be worked out by scrolling. A
    /// page that says "Downloading (3)" has answered the question the reader arrived with;
    /// one that shows three rows makes them do the counting, and makes "nothing here" and
    /// "nothing loaded" look identical.
    /// </para>
    /// </summary>
    public static PluginComponent Section(string id, string heading, string? note, PluginComponent body)
    {
        List<PluginComponent> children = [Text($"{id}-heading", heading, "subtitle")];

        if (!string.IsNullOrWhiteSpace(note))
            children.Add(Text($"{id}-note", note, "caption"));

        children.Add(body);

        return Container(id, children);
    }

    /// <summary>
    /// A real table: aligned columns, a rule under every row, and one heading per column.
    ///
    /// <para>
    /// Not <c>PluginViews.Table</c>. That builds the design system's rendering - it expands
    /// each cell into a node of its own and sends the whole thing as an <c>NMCard</c>, which
    /// is the mismatch the rest of this file exists to route around. The client's own
    /// <c>PluginTable</c> reads two props: the columns, and rows whose values sit in their
    /// props under the column's key.
    /// </para>
    ///
    /// <para>
    /// This is what a page a reader scans should be. A list of wrapping rows re-flows
    /// differently for every row it draws, so nothing lines up and the eye has to start
    /// again on each one; a table has columns, which is the entire point.
    /// </para>
    /// </summary>
    public static PluginComponent Table(
        string id,
        IReadOnlyList<PluginTableColumn> columns,
        IEnumerable<PluginComponent> rows,
        string? emptyMessage = null) =>
        new()
        {
            Id = id,
            Component = TableComponent,
            Props = new() { ["columns"] = columns, ["emptyMessage"] = emptyMessage },
            Items = [.. rows],
        };

    /// <summary>
    /// One row, with each value under the key of the column that draws it.
    ///
    /// <para>
    /// A badge cell reads its variant from <c>{key}Variant</c>, a progress cell wants a
    /// fraction, and bytes, rates and durations want the raw number - the client formats
    /// those in the reader's own locale, which a plugin cannot do for them.
    /// </para>
    ///
    /// <para>
    /// The component name is never resolved: a table draws its rows itself rather than
    /// sending them back through the node lookup. It says row anyway, so a client that
    /// walks the tree without a table of its own draws an empty row instead of an
    /// unknown-tag apology.
    /// </para>
    /// </summary>
    public static PluginComponent TableRow(
        string id,
        Dictionary<string, object?> cells,
        PluginActionIntent? action = null) =>
        new()
        {
            Id = id,
            Component = RowComponent,
            Props = cells,
            Action = action,
        };

    /// <summary>
    /// One thing, with a heading of its own and whatever it needs underneath.
    ///
    /// <para>
    /// The shape for something that is not a row in a list: a download has a name, a line
    /// about where it stands, a bar, and two buttons, and squeezing that into a wrapping row
    /// is how the downloads page ended up unreadable.
    /// </para>
    /// </summary>
    public static PluginComponent Detail(
        string id,
        string title,
        string? description,
        params PluginComponent[] items) =>
        new()
        {
            Id = id,
            Component = DetailComponent,
            Props = new() { ["title"] = title, ["description"] = description },
            Items = [.. items],
        };

    public static PluginComponent Badge(string id, string label, string variant) =>
        new()
        {
            Id = id,
            Component = BadgeComponent,
            Props = new() { ["label"] = label, ["variant"] = variant },
        };
}
