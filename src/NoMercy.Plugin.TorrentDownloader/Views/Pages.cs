using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Views;

/// <summary>
/// Where this plugin appears, and what it puts there.
/// </summary>
public static class Pages
{
    /// <summary>Everything the plugin is doing. Beside the libraries.</summary>
    public const string DashboardRoute = "/";

    /// <summary>Under the plugin settings list, where the owner expects it.</summary>
    public const string SettingsRoute = "/settings";

    /// <summary>Every show with anything outstanding. Reached from the dashboard.</summary>
    public const string ShowsRoute = "/shows";

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
        new PluginRoute
        {
            Path = DashboardRoute,
            Name = "dashboard",
            Label = "Torrent Downloader",
            Layout = PluginLayout.Standard,
        },
        new PluginRoute
        {
            Path = ShowsRoute,
            Name = "shows",
            Label = "Shows",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = QueueRoute,
            Name = "queue",
            Label = "Queue",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = DownloadsRoute,
            Name = "downloads",
            Label = "Downloads",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = HistoryRoute,
            Name = "history",
            Label = "History",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = SkippedRoute,
            Name = "skipped",
            Label = "Skipped",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = SourcesRoute,
            Name = "sources",
            Label = "Sources",
            Layout = PluginLayout.ListDetail,
        },
        new PluginRoute
        {
            Path = SettingsRoute,
            Name = "settings",
            Label = "Settings",
            Layout = PluginLayout.Form,
        });

    /// <summary>
    /// The mounts in <c>plugin.json</c>, entry for entry — a test holds the two
    /// together, because a mount the plugin has no entry for is a link to a
    /// page it will not serve.
    /// </summary>
    public static IReadOnlyList<PluginNavEntry> NavEntries { get; } =
    [
        new()
        {
            Section = PluginUiSection.Library,
            Label = PluginIdentity.Name,
            Icon = "download",
            Route = DashboardRoute,
        },
        new()
        {
            Section = PluginUiSection.Settings,
            Label = PluginIdentity.Name,
            Icon = "download",
            Route = SettingsRoute,
        },
    ];
}
