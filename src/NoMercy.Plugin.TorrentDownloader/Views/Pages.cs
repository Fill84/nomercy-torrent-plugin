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

    /// <summary>
    /// What a viewer sees today: which version of this plugin the server has
    /// actually loaded.
    /// </summary>
    /// <remarks>
    /// True, and the only thing that is true yet — the dashboard arrives in
    /// S0-04 and the settings page in S0-05. It is worth a page of its own
    /// meanwhile: a deploy onto a running server copies nothing and looks
    /// exactly like one that worked, and this is where that shows.
    /// </remarks>
    public static PluginView Loaded()
    {
        return PluginViews.Declarative(
            PluginViews.Text("version", $"{PluginIdentity.Name} {PluginIdentity.Version}", "title"));
    }
}
