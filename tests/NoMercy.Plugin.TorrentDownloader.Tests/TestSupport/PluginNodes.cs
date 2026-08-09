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
        All(view).SelectMany(TextOf).Where(word => word.Length > 0);

    public static IEnumerable<string> Words(PluginComponent node) =>
        Flatten(node).SelectMany(TextOf).Where(word => word.Length > 0);

    private static IEnumerable<string> TextOf(PluginComponent node) =>
        [Prop(node, "value"), Prop(node, "title"), Prop(node, "message"), Prop(node, "label")];

    private static string Prop(PluginComponent node, string key) =>
        node.Props.TryGetValue(key, out object? value) ? value?.ToString() ?? "" : "";
}
