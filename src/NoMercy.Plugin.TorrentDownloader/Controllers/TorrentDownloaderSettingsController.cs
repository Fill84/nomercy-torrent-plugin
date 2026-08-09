// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

// PluginRouteConvention prefixes every route below with api/plugins/{this plugin's id},
// taken from the assembly the controller came from - not something this class can see or
// choose. Each [HttpPost] below is the other half of an agreement with SettingsView: its
// general form calls CallPlugin(SettingsView.SaveSettingsMethod), and its indexer/client
// forms call CallPlugin($"{SaveIndexerMethod}/{index}") / CallPlugin($"{SaveClientMethod}
// /{index}") - the client interpolates that method straight into the request path and never
// forwards an action intent's payload, so the entry's index has to travel in the route
// rather than the body. The shared constants are what keep the method strings SettingsView
// builds and the routes below from drifting into two different literals.
//
// IPluginManager is the sanctioned way to reach the live plugin: it is registered as a
// singleton for the whole host by PluginServiceCollectionExtensions.AddPluginSystem, and
// ASP.NET Core constructs this controller per request from that same container, so
// injecting it here needs nothing this plugin has to invent. IPluginServiceRegistrator was
// considered and rejected - it runs during a one-shot, pre-build discovery pass against an
// instance created with a bare Activator.CreateInstance in a throwaway load context, before
// IPluginContext or the real plugin instance exist, so it has nothing live to hand out. The
// running instance, with its already-Initialize()'d context, is only reachable afterwards,
// through IPluginManager.GetPluginInstance.
public sealed class TorrentDownloaderSettingsController(IPluginManager pluginManager) : PluginControllerBase
{
    [HttpPost(SettingsView.SaveSettingsMethod)]
    public Task<IActionResult> SaveSettings([FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SaveSettingsAsync(request, ct));

    [HttpPost(SettingsView.SaveIndexerRouteTemplate)]
    public Task<IActionResult> SaveIndexer(int index, [FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SaveIndexerAsync(index, request, ct));

    [HttpPost(SettingsView.SaveClientRouteTemplate)]
    public Task<IActionResult> SaveClient(int index, [FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SaveClientAsync(index, request, ct));

    [HttpPost(SettingsView.SavePrivateTrackerRouteTemplate)]
    public Task<IActionResult> SavePrivateTracker(int index, [FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SavePrivateTrackerAsync(index, request, ct));

    [HttpPost(SettingsView.AddPrivateTrackerMethod)]
    public Task<IActionResult> AddPrivateTracker(CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddPrivateTrackerAsync(ct));

    [HttpPost(SettingsView.RemovePrivateTrackerRouteTemplate)]
    public Task<IActionResult> RemovePrivateTracker(int index, CancellationToken ct) =>
        RespondAsync(plugin => plugin.RemovePrivateTrackerAsync(index, ct));

    // Keyed by the library's show id, not a render index: the downloads page is the only
    // caller and the list it renders changes shape the moment a show is followed.
    [HttpPost(DownloadsView.FollowShowRouteTemplate)]
    public Task<IActionResult> FollowShow(int showId, CancellationToken ct) =>
        RespondAsync(plugin => plugin.FollowShowAsync(showId, ct));

    [HttpPost(DownloadsView.UnfollowShowRouteTemplate)]
    public Task<IActionResult> UnfollowShow(int showId, CancellationToken ct) =>
        RespondAsync(plugin => plugin.UnfollowShowAsync(showId, ct));

    [HttpPost(SettingsView.AddIndexerMethod)]
    public Task<IActionResult> AddIndexer(CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddIndexerAsync(ct));

    [HttpPost(SettingsView.AddClientMethod)]
    public Task<IActionResult> AddClient(CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddClientAsync(ct));

    [HttpPost(SettingsView.RemoveIndexerRouteTemplate)]
    public Task<IActionResult> RemoveIndexer(int index, CancellationToken ct) =>
        RespondAsync(plugin => plugin.RemoveIndexerAsync(index, ct));

    [HttpPost(SettingsView.RemoveClientRouteTemplate)]
    public Task<IActionResult> RemoveClient(int index, CancellationToken ct) =>
        RespondAsync(plugin => plugin.RemoveClientAsync(index, ct));

    private async Task<IActionResult> RespondAsync(Func<TorrentDownloaderPlugin, Task<SaveSettingsOutcome>> save)
    {
        if (pluginManager.GetPluginInstance(PluginId) is not TorrentDownloaderPlugin plugin)
        {
            return NotFound();
        }

        SaveSettingsOutcome outcome = await save(plugin);

        return outcome.Succeeded
            ? Status<object?>(null, message: "Settings saved.")
            : Status<object?>(null, status: "error", message: outcome.Error);
    }
}
