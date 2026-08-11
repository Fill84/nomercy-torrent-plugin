// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Reflection;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Reading a built view the way the dashboard does.
///
/// <para>
/// This used to read the design system's rendering, because that is what the contract's
/// own helpers produce: a form became an NMCard whose fields were turned into child
/// components. The client never drew any of it - it keys plugin components by their own
/// names and those nodes were never reached. See <see cref="Ui"/> for the whole account.
/// </para>
///
/// <para>
/// So a form's fields are a <c>fields</c> prop again, exactly as the plugin authored them,
/// and these helpers read them there. That is also the shape the client submits from,
/// which is the point: a test that reads the authored form is now reading the thing that
/// actually posts.
/// </para>
/// </summary>
internal static class PluginNodes
{
    public static IEnumerable<PluginComponent> Flatten(PluginComponent node)
    {
        yield return node;

        foreach (PluginComponent child in node.Items.SelectMany(Flatten))
        {
            yield return child;
        }
    }

    public static IEnumerable<PluginComponent> All(PluginView view) =>
        (view.Components ?? []).SelectMany(Flatten);

    /// <summary>
    /// Every tag the dashboard can draw: the ones <see cref="Ui"/> names, reflected rather
    /// than listed so a component added there is known here without a second edit.
    /// </summary>
    public static IReadOnlySet<string> KnownComponents { get; } =
        new HashSet<string>(
            typeof(Ui)
                .GetFields(BindingFlags.Public | BindingFlags.Static)
                .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                .Select(field => (string)field.GetRawConstantValue()!),
            StringComparer.Ordinal
        );

    public static IEnumerable<PluginComponent> Forms(PluginView view) =>
        All(view).Where(node => node.Component == Ui.FormComponent);

    /// <summary>A form's fields, as the plugin authored them and as the client submits them.</summary>
    public static IEnumerable<PluginFormField> Fields(PluginComponent form) =>
        form.Props.TryGetValue("fields", out object? fields) && fields is IEnumerable<PluginFormField> typed
            ? typed
            : [];

    public static IEnumerable<PluginFormField> AllFields(PluginView view) =>
        Forms(view).SelectMany(Fields);

    public static string Name(PluginFormField field) => field.Name;

    public static string Type(PluginFormField field) => field.Type;

    public static object? Value(PluginFormField field) => field.Value;

    public static string Placeholder(PluginFormField field) => field.Placeholder ?? "";

    /// <summary>A toggle's state. Authored as the field's value, which is a bool for a toggle.</summary>
    public static bool Checked(PluginFormField field) => field.Value is true;

    /// <summary>
    /// Every word in the view, wherever the component that carries it keeps it. Each
    /// component names its own text prop - a text node has "value", an empty state has a
    /// "title" and a "message", a button and a badge have a "label" - so a helper looking
    /// for one of them finds nothing on a page full of words.
    /// </summary>
    public static IEnumerable<string> Words(PluginView view) =>
        All(view).SelectMany(Said).Where(word => word.Length > 0);

    public static IEnumerable<string> Words(PluginComponent node) =>
        Flatten(node).SelectMany(Said).Where(word => word.Length > 0);

    private static IEnumerable<string> Said(PluginComponent node) => [.. TextOf(node), .. CellsOf(node)];

    /// <summary>
    /// A table's own words.
    ///
    /// <para>
    /// A table's rows do not carry their text under a prop name anyone can guess: each value
    /// sits under the key of the column that draws it, so the only way to read a table the
    /// way a viewer does is to read it through its columns. Which is also the assertion
    /// worth having - a value under a key no column names is a value nobody sees.
    /// </para>
    /// </summary>
    private static IEnumerable<string> CellsOf(PluginComponent node)
    {
        if (node.Component != Ui.TableComponent)
            yield break;

        if (!node.Props.TryGetValue("columns", out object? declared)
            || declared is not IEnumerable<PluginTableColumn> columns)
        {
            yield break;
        }

        List<PluginTableColumn> keys = [.. columns];

        foreach (PluginComponent row in node.Items)
        {
            foreach (PluginTableColumn column in keys)
            {
                if (row.Props.TryGetValue(column.Key, out object? value) && value is string text)
                    yield return text;
            }
        }
    }

    /// <summary>Every row of a table, whichever page drew it.</summary>
    public static IEnumerable<PluginComponent> TableRows(PluginView view) =>
        All(view).Where(node => node.Component == Ui.TableComponent).SelectMany(table => table.Items);

    /// <summary>One cell, read the way the column that draws it would.</summary>
    public static object? Cell(PluginComponent row, string key) =>
        row.Props.TryGetValue(key, out object? value) ? value : null;

    /// <summary>Whether the page says this anywhere, in any component that carries words.</summary>
    public static bool Says(PluginView view, string text) =>
        Words(view).Any(word => word.Contains(text, StringComparison.Ordinal));

    // Every prop a component draws words from: a text node has "value", an empty state a
    // "title" and a "message", a button and a badge a "label", a card a "subtitle", and a
    // detail block a "description". A helper that knew only some of them would report a page
    // as silent about something it says in full.
    private static IEnumerable<string> TextOf(PluginComponent node) =>
    [
        Prop(node, "value"),
        Prop(node, "title"),
        Prop(node, "subtitle"),
        Prop(node, "description"),
        Prop(node, "message"),
        Prop(node, "label"),
    ];

    private static string Prop(PluginComponent node, string key) =>
        node.Props.TryGetValue(key, out object? value) ? value?.ToString() ?? "" : "";
}
