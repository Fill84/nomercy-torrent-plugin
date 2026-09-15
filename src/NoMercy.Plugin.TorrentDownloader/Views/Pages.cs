using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Where this plugin appears, and what it puts there.
/// </summary>
public static class Pages
{
    /// <summary>Every show and anime, one list per library, each switched on one by one.</summary>
    public const string OverviewRoute = "/";

    /// <summary>What a run is doing: the stages, what is in flight, and the notes per episode.</summary>
    public const string ActivityRoute = "/activity";

    /// <summary>The settings form of one show, by the server's show id.</summary>
    public const string ShowSettingsRoute = "/shows/:id";

    public const string ShowSettingsName = "show";

    /// <summary>The preferences form of one library, by the server's library id.</summary>
    public const string LibraryPreferencesRoute = "/libraries/:id";

    public const string LibraryPreferencesName = "library";

    /// <summary>One page of one library's shows, which is where the overview's Next goes.</summary>
    public const string LibraryShowsRoute = "/libraries/:id/shows/:page";

    public const string LibraryShowsName = "library-shows";

    /// <summary>Under the plugin settings list, where the owner expects it.</summary>
    public const string SettingsRoute = "/settings";

    /// <summary>What is being looked for, given up on, and still to air.</summary>
    public const string QueueRoute = "/queue";

    /// <summary>What is transferring, and what was grabbed and is not yet.</summary>
    public const string DownloadsRoute = "/downloads";

    /// <summary>Grabbed, skipped, failed, dispatched and allowed, newest first.</summary>
    public const string HistoryRoute = "/history";

    /// <summary>What the profile or the blacklist refused, and the control to overrule it.</summary>
    public const string SkippedRoute = "/skipped";

    /// <summary>Per source: what it last answered, and when it is next askable.</summary>
    public const string SourcesRoute = "/sources";

    /// <summary>
    /// The same page with the plugin's own navigation above it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The server mounts two of these eight — the dashboard beside the
    /// libraries, settings under the settings list — and the other six were
    /// reachable by typing an address and by nothing else. A page nobody can
    /// leave is barely a page at all, and one nobody can arrive at is none.
    /// </para>
    /// <para>
    /// Wrapped here, once, rather than added to each view. Eight views each
    /// remembering to carry it is eight chances to forget, and the one that
    /// forgot would be a dead end nothing else could tell you about.
    /// </para>
    /// </remarks>
    public static PluginView WithNavigation(PluginView page, string route)
    {
        return new()
        {
            Layout = page.Layout,
            Components = [Navigation(route), .. page.Components ?? []],
        };
    }

    /// <summary>A way to every page, with the one being read marked.</summary>
    private static PluginComponent Navigation(string route)
    {
        return Ui.Row(
            "nav",
            [
                // Only the pages that stand on their own. A page with a parameter in its path - one
                // show's settings, one library's page of shows - is reached from the row or the block
                // it belongs to, and has no address a tab could hold.
                .. Navigable.Select(one => Ui.Button(
                    $"nav-{one.Name}",
                    one.Label ?? one.Name,
                    PluginActionIntent.Navigate(one.Path),

                    // The page being read is marked rather than left out. A
                    // link that disappears on arrival moves every other link
                    // along by one, so the row is never twice in the same
                    // place and nothing can be found by where it sits.
                    variant: Same(one.Path, route) ? "primary" : "ghost")),
            ]);
    }

    /// <summary>The routes a tab can point at: every one without a parameter in its path.</summary>
    public static IEnumerable<PluginRoute> Navigable => Routes.Routes.Where(route => !route.Path.Contains(':', StringComparison.Ordinal));

    /// <summary>
    /// Whether two routes are the same page.
    /// </summary>
    /// <remarks>
    /// A trailing slash is the difference between what the server asks for and
    /// what the table declares, and it is not a difference to the reader.
    /// </remarks>
    private static bool Same(string path, string route)
    {
        return string.Equals(
            path.TrimEnd('/'),
            route.TrimEnd('/'),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The pages this plugin serves, declared rather than matched inside the
    /// view.
    /// </summary>
    /// <remarks>
    /// A route that exists only as a case in a switch is one nothing else can
    /// see: declared, the server can list what a viewer can reach, hand each
    /// page the shell it wants, and refuse a link to a page that does not
    /// exist. Only two of these are mounted in navigation — the rest are
    /// reached from the dashboard, which is why the mounts and
    /// <see cref="NavEntries"/> are a shorter list than this one.
    /// </remarks>
    public static PluginRouteTable Routes { get; } = new(
        Page(OverviewRoute, "overview", "Overview"),
        Page(ActivityRoute, "activity", "Activity"),
        Page(QueueRoute, "queue", "Queue"),
        Page(DownloadsRoute, "downloads", "Downloads"),
        Page(HistoryRoute, "history", "History"),
        Page(SkippedRoute, "skipped", "Skipped"),
        Page(SourcesRoute, "sources", "Sources"),
        Page(SettingsRoute, "settings", "Settings"),
        Page(ShowSettingsRoute, ShowSettingsName, "Show settings"),
        Page(LibraryPreferencesRoute, LibraryPreferencesName, "Library preferences"),
        Page(LibraryShowsRoute, LibraryShowsName, "Shows"));

    /// <summary>One page, in the dashboard's width like every other.</summary>
    private static PluginRoute Page(string path, string name, string label)
    {
        return new()
        {
            Path = path,
            Name = name,
            Label = label,
            Layout = PluginLayout.Wide,
        };
    }

    /// <summary>
    /// The mounts in <c>plugin.json</c>, entry for entry — a test holds the two
    /// together, because a mount the plugin has no entry for is a link to a
    /// page it will not serve.
    /// </summary>
    public static IReadOnlyList<PluginNavEntry> NavEntries { get; } =
    [
        new()
        {
            // The section is what the server turns into the address the client
            // links to, and the plugin never learns its own prefix. Dashboard is
            // what the word means: server administration beside the other
            // owner-only panels, where settings is where a plugin is set up
            // rather than where it works.
            Section = PluginUiSection.Dashboard,
            Label = PluginIdentity.Name,
            Icon = "download",
            Route = OverviewRoute,
        },
        new()
        {
            // Not beside the libraries as well: the owner's decision of
            // 11 September 2026, the plugin belongs in the dashboard and the user
            // menu and nowhere else. Settings is the mount the web app shows in
            // the user menu.
            Section = PluginUiSection.Settings,
            Label = PluginIdentity.Name,
            Icon = "download",
            Route = SettingsRoute,
        },
    ];
}
