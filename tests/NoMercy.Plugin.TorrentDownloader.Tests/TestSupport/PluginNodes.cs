// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Reflection;
using NoMercy.Design;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Reading a built view the way a client does.
///
/// <para>
/// The design system draws every container as <c>NMCard</c> and turns what a
/// view authored as props into components: a form's fields are its children
/// now, not a "fields" bag on the form, and a label is a text node inside the
/// thing it labels rather than a string beside it. A test that reads the
/// authored shape is reading something no client ever receives.
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
    /// Every tag a client is expected to draw: the plugin contract's own set, the
    /// media components the app's screens are built from, and the design system's
    /// fifty-six. Reflected rather than listed, so a component added to the design
    /// system is known here the moment the contract is repacked - the alternative
    /// is a literal list that fails on a component that renders perfectly well.
    /// </summary>
    public static IReadOnlySet<string> KnownComponents { get; } =
        new HashSet<string>(
            PluginComponentType
                .All.Concat(NmAppComponents.All)
                .Concat(
                    typeof(NmComponents)
                        .GetFields(BindingFlags.Public | BindingFlags.Static)
                        .Where(field => field.IsLiteral && field.FieldType == typeof(string))
                        .Select(field => (string)field.GetRawConstantValue()!)
                ),
            StringComparer.Ordinal
        );

    /// <summary>A form: the card the submit button belongs to.</summary>
    public static IEnumerable<PluginComponent> Forms(PluginView view) =>
        All(view).Where(node => node.Items.Any(child => child.Id == $"{node.Id}-submit"));

    public static PluginComponent Submit(PluginComponent form) =>
        form.Items.Single(child => child.Id == $"{form.Id}-submit");

    /// <summary>
    /// A form's fields, as the renderer leaves them: an input under a ghost group,
    /// or a toggle standing on its own. Both carry the authored name, which is what
    /// still ties a rendered control back to the field the view asked for.
    /// </summary>
    public static IEnumerable<PluginComponent> Fields(PluginComponent form) =>
        form.Items.SelectMany(Flatten).Where(node => node.Props.ContainsKey("name"));

    public static IEnumerable<PluginComponent> AllFields(PluginView view) =>
        Forms(view).SelectMany(Fields);

    public static string Name(PluginComponent field) => Prop(field, "name");

    /// <summary>
    /// What kind of control this is. A password and a number are inputs wearing a
    /// "type"; a toggle and a checkbox are their own components and carry none.
    /// </summary>
    public static string Type(PluginComponent field) =>
        field.Component switch
        {
            "NMToggle" => PluginFormFieldType.Toggle,
            "NMCheckbox" => PluginFormFieldType.Checkbox,
            "NMSelect" => PluginFormFieldType.Select,
            "NMFileUpload" => PluginFormFieldType.File,
            _ => field.Props.TryGetValue("type", out object? type)
                ? type as string ?? PluginFormFieldType.Text
                : PluginFormFieldType.Text,
        };

    public static object? Value(PluginComponent field) =>
        field.Props.TryGetValue("value", out object? value) ? value : null;

    /// <summary>
    /// A toggle's or checkbox's state. The renderer writes it to "checked" and gives the
    /// node no "value" at all, so a test reaching for <see cref="Value"/> reads null and
    /// passes for the wrong reason no matter which way the toggle is set.
    /// </summary>
    public static bool Checked(PluginComponent field) =>
        field.Props.TryGetValue("checked", out object? value) && value is true;

    public static string Placeholder(PluginComponent field) => Prop(field, "placeholder");

    /// <summary>
    /// Every word in the view, wherever the renderer put it: a text leaf's own
    /// content, and the helper line that a caption became.
    /// </summary>
    public static IEnumerable<string> Words(PluginView view) =>
        All(view)
            .SelectMany(node => new[] { Prop(node, "text"), Prop(node, "helperText") })
            .Where(word => word.Length > 0);

    public static IEnumerable<string> Words(PluginComponent node) =>
        Flatten(node)
            .SelectMany(child => new[] { Prop(child, "text"), Prop(child, "helperText") })
            .Where(word => word.Length > 0);

    private static string Prop(PluginComponent node, string key) =>
        node.Props.TryGetValue(key, out object? value) ? value?.ToString() ?? "" : "";
}
