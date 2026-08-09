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

    public static PluginComponent Badge(string id, string label, string variant) =>
        new()
        {
            Id = id,
            Component = BadgeComponent,
            Props = new() { ["label"] = label, ["variant"] = variant },
        };
}
