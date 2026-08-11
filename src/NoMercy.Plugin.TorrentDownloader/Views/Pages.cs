// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// The plugin's map of itself: every page it serves, and the bar that walks between them.
///
/// <para>
/// Declared rather than matched inside <c>GetViewAsync</c>, because a route that only
/// exists as a case in a switch is one nothing else can see. The server reads this table
/// and serves it as the plugin's pages; the client registers a named route for each. A page
/// nobody declares falls back to a wildcard that covers the legacy <c>/plugins/{id}/…</c>
/// mount and not the <c>/video/plugins/{id}/…</c> one this plugin actually sits behind - so
/// an undeclared page is not a page, it is the app's 404.
/// </para>
///
/// <para>
/// Every link is built from this table by name, so a path is written once. Rewriting a path
/// moves every tab that leads there.
/// </para>
/// </summary>
public static class Pages
{
    public const string Overview = "overview";
    public const string Shows = "shows";
    public const string Downloads = "downloads";
    public const string Queue = "queue";
    public const string History = "history";
    public const string Sources = "sources";
    public const string Skipped = "skipped";
    public const string Settings = "settings";

    /// <summary>
    /// One source, on its own page.
    ///
    /// <para>
    /// Not a tab. A source is reached from the list rather than from the bar, the way a
    /// library page is reached from a library - putting every source in the bar would make
    /// the bar grow with the configuration.
    /// </para>
    /// </summary>
    public const string Source = "source";

    /// <summary>One download, with the buttons that change it. Reached from the list, never from the bar.</summary>
    public const string Download = "download";

    /// <summary>One show, on its own page. Reached from the list, like a source.</summary>
    public const string Show = "show";

    /// <summary>
    /// The pages, in the order the tab bar offers them: what is happening, then what is
    /// being worked on, then what happened, then what it is all configured from.
    ///
    /// <para>
    /// Shows is deliberately absent. It needs per-show grouping that does not exist yet, and
    /// a tab named Shows that listed only the few the plugin is leaving alone would mislead
    /// worse than no tab. Until it arrives, that list is on the overview under a heading
    /// that says what it is.
    /// </para>
    /// </summary>
    public static PluginRouteTable Routes { get; } = new(
        new PluginRoute { Path = "/", Name = Overview, Label = "Overview" },
        new PluginRoute { Path = "/shows", Name = Shows, Label = "Shows" },
        new PluginRoute { Path = "/shows/:showId", Name = Show, Label = "Show" },
        new PluginRoute { Path = "/downloads", Name = Downloads, Label = "Downloads" },
        new PluginRoute { Path = "/queue", Name = Queue, Label = "Queue" },
        new PluginRoute { Path = "/history", Name = History, Label = "History" },
        new PluginRoute { Path = "/sources", Name = Sources, Label = "Sources" },
        new PluginRoute { Path = "/skipped", Name = Skipped, Label = "Skipped" },

        // No layout on these two either, and that is the point.
        //
        // They asked for PluginLayout.Form, on the reasoning that a form reads badly at full
        // width. The client obliges: the form shell is a 40rem column against the standard
        // 64rem one. But every page in this plugin carries the same bar of eight tabs, and
        // eight tabs do not fit in 40rem - so opening Settings shrank the page under the
        // navigation and broke the bar onto two lines, and going back widened it again.
        // Everything below the bar jumped by a row each way.
        //
        // A page that is one tab of eight has to be the width of the other seven. The form
        // measure is for a page that is only a form, and neither of these is: they are
        // sections of a plugin you move around inside.
        new PluginRoute { Path = "/settings", Name = Settings, Label = "Settings" },
        new PluginRoute { Path = "/sources/:index", Name = Source, Label = "Source" },

        // Pausing and cancelling live here rather than on every row of the list. A table
        // cell cannot hold a button, and making the row itself the action would put "delete
        // this download and blacklist the release" one stray click away.
        new PluginRoute { Path = "/downloads/:infoHash", Name = Download, Label = "Download" });

    /// <summary>
    /// What the bar offers, which is not everything the plugin serves.
    ///
    /// <para>
    /// A page reached from a list belongs in that list, not in the navigation. A bar built
    /// from the whole route table would grow an entry per configured source.
    /// </para>
    /// </summary>
    private static readonly string[] TabOrder = [Overview, Shows, Downloads, Queue, History, Sources, Skipped, Settings];

    /// <summary>
    /// A page: its own name, the bar, then whatever it is about.
    ///
    /// <para>
    /// Every view goes through here rather than building its own root, so the bar cannot be
    /// forgotten on one page - which on a plugin whose sidebar section draws nothing would
    /// leave that page with no way out but the browser's back button.
    /// </para>
    /// </summary>
    public static PluginView Page(string name, int refreshSeconds, params PluginComponent[] body) =>
        Page(name, null, refreshSeconds, body);

    /// <inheritdoc cref="Page(string, int, PluginComponent[])"/>
    /// <param name="heading">
    /// What this page is called, when that is not the page's own name - a source's page is
    /// headed by the source, not by the word "Source".
    /// </param>
    public static PluginView Page(string name, string? heading, int refreshSeconds, params PluginComponent[] body) =>
        PluginViews.Declarative(
            refreshSeconds,
            Ui.Container(
                $"{name}-root",
                [
                    Ui.Text($"{name}-heading", heading ?? LabelOf(name), "title"),
                    Tabs(name),
                    .. body,
                ]));

    /// <summary>
    /// One entry per page, with the one you are on drawn as a badge rather than a button.
    ///
    /// <para>
    /// Not a variant: <c>PluginButton</c> draws everything but "danger" as the same grey
    /// button, so a variant cannot say which tab is current. A badge is a pill in the theme's
    /// own palette, and reads as a label rather than as something to press - which is what
    /// the page you are already on is.
    /// </para>
    /// </summary>
    public static PluginComponent Tabs(string current) =>
        Ui.Row(
            "tabs",
            TabOrder.Select(name => name == current
                ? Ui.Badge($"tab-{name}", LabelOf(name), PluginBadgeVariant.Info)
                : Ui.Button($"tab-{name}", LabelOf(name), Routes.GoTo(name))));

    /// <summary>
    /// What a page is called. One spelling, so a tab and the heading it leads to cannot
    /// disagree about which page the viewer is looking at.
    /// </summary>
    public static string LabelOf(string name) =>
        Routes.Routes.FirstOrDefault(route => route.Name == name)?.Label ?? name;

    /// <summary>
    /// What the last button press did, put on the page that gets drawn afterwards.
    ///
    /// <para>
    /// The client posts an action, discards the response body whatever it says, and
    /// re-fetches the view. So every sentence this plugin writes to explain a refusal - "a
    /// site's search address needs {query}", "paste a magnet link first", "there is already
    /// a source called that" - went nowhere, and a rejected form looked exactly like a form
    /// that had saved. That was observed: a site address pasted without the placeholder was
    /// refused, correctly, and the page said nothing at all.
    /// </para>
    ///
    /// <para>
    /// Inserted here rather than by each view, for the same reason the tab bar is: it is
    /// something every page needs and none of them should have to remember. It goes above
    /// the content and below the tabs, where a reader is already looking after pressing
    /// something.
    /// </para>
    /// </summary>
    public static PluginView WithNotice(PluginView view, string? message, bool failed)
    {
        if (string.IsNullOrWhiteSpace(message) || view.Components is not [PluginComponent root, ..])
            return view;

        root.Items.Insert(
            Math.Min(2, root.Items.Count),
            Ui.Row(
                "notice",
                Ui.Badge("notice-badge", failed ? "Not done" : "Done", failed ? PluginBadgeVariant.Warning : PluginBadgeVariant.Success),
                Ui.Text("notice-text", message)));

        return view;
    }
}
