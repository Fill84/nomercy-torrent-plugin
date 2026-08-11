// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Mvc;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

// PluginRouteConvention prefixes every route below with api/plugins/{this plugin's id},
// taken from the assembly the controller came from - not something this class can see or
// choose. Each [HttpPost] below is the other half of an agreement with a page: a form calls
// CallPlugin(PluginMethods.SaveSettings), a per-entry form calls
// CallPlugin($"{PluginMethods.SaveIndexer}/{index}"). The client interpolates that method
// straight into the request path and never forwards an action intent's payload, so the
// entry's index has to travel in the route rather than the body. PluginMethods is what keeps
// the strings a page builds and the routes below from drifting into two different literals -
// and it lives outside the views precisely so that moving a button from one page to another
// does not touch this file.
//
// IPluginManager is the sanctioned way to reach the live plugin: it is registered as a
// singleton for the whole host by PluginServiceCollectionExtensions.AddPluginSystem, and
// ASP.NET Core constructs this controller per request from that same container, so injecting
// it here needs nothing this plugin has to invent. IPluginServiceRegistrator was considered
// and rejected - it runs during a one-shot, pre-build discovery pass against an instance
// created with a bare Activator.CreateInstance in a throwaway load context, before
// IPluginContext or the real plugin instance exist, so it has nothing live to hand out. The
// running instance, with its already-Initialize()'d context, is only reachable afterwards,
// through IPluginManager.GetPluginInstance.
public sealed class TorrentDownloaderSettingsController(IPluginManager pluginManager) : PluginControllerBase
{
    [HttpPost(PluginMethods.SaveSettings)]
    public Task<IActionResult> SaveSettings([FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SaveSettingsAsync(request, ct));

    [HttpPost(PluginMethods.SaveIndexerRoute)]
    public Task<IActionResult> SaveIndexer(int index, [FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SaveIndexerAsync(index, request, ct));

    [HttpPost(PluginMethods.SavePrivateTrackerRoute)]
    public Task<IActionResult> SavePrivateTracker(int index, [FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SavePrivateTrackerAsync(index, request, ct));

    [HttpPost(PluginMethods.AddPrivateTracker)]
    public Task<IActionResult> AddPrivateTracker(CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddPrivateTrackerAsync(ct));

    [HttpPost(PluginMethods.RemovePrivateTrackerRoute)]
    public Task<IActionResult> RemovePrivateTracker(int index, CancellationToken ct) =>
        RespondAsync(plugin => plugin.RemovePrivateTrackerAsync(index, ct));

    // Keyed by the library's show id, not a render index: the list that carries these
    // buttons changes shape the moment a show is followed.
    [HttpPost(PluginMethods.FollowShowRoute)]
    public Task<IActionResult> FollowShow(int showId, CancellationToken ct) =>
        RespondAsync(plugin => plugin.FollowShowAsync(showId, ct));

    [HttpPost(PluginMethods.UnfollowShowRoute)]
    public Task<IActionResult> UnfollowShow(int showId, CancellationToken ct) =>
        RespondAsync(plugin => plugin.UnfollowShowAsync(showId, ct));

    // A form rather than a row, so the name arrives in the body like every other form's
    // fields do.
    [HttpPost(PluginMethods.FollowByName)]
    public Task<IActionResult> FollowByName([FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.FollowByNameAsync(request, ct));

    [HttpPost(PluginMethods.PauseDownloadRoute)]
    public Task<IActionResult> PauseDownload(string infoHash, CancellationToken ct) =>
        RespondAsync(plugin => plugin.PauseDownloadAsync(infoHash, ct));

    [HttpPost(PluginMethods.ResumeDownloadRoute)]
    public Task<IActionResult> ResumeDownload(string infoHash, CancellationToken ct) =>
        RespondAsync(plugin => plugin.ResumeDownloadAsync(infoHash, ct));

    [HttpPost(PluginMethods.CancelDownloadRoute)]
    public Task<IActionResult> CancelDownload(string infoHash, CancellationToken ct) =>
        RespondAsync(plugin => plugin.CancelDownloadAsync(infoHash, ct));

    [HttpPost(PluginMethods.AllowReleaseRoute)]
    public Task<IActionResult> AllowRelease(string handle, CancellationToken ct) =>
        RespondAsync(plugin => plugin.AllowReleaseAsync(handle, ct));

    [HttpPost(PluginMethods.SearchNowRoute)]
    public Task<IActionResult> SearchNow(int showId, int season, int episode, CancellationToken ct) =>
        RespondAsync(plugin => plugin.SearchNowAsync(showId, season, episode, ct));

    [HttpPost(PluginMethods.AddTorrent)]
    public Task<IActionResult> AddTorrent([FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddTorrentAsync(request, ct));

    [HttpPost(PluginMethods.AddSource)]
    public Task<IActionResult> AddSource([FromBody] SaveSettingsRequest request, CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddSourceAsync(request, ct));

    // No page draws a button for this any more - the sources page adds a source complete in
    // one go instead of appending a blank entry to be found and filled in. The endpoint
    // stays: a plugin that answers one fewer call than it did yesterday is a plugin that
    // breaks whoever was calling it.
    [HttpPost(PluginMethods.AddIndexer)]
    public Task<IActionResult> AddIndexer(CancellationToken ct) =>
        RespondAsync(plugin => plugin.AddIndexerAsync(ct));

    [HttpPost(PluginMethods.RemoveIndexerRoute)]
    public Task<IActionResult> RemoveIndexer(int index, CancellationToken ct) =>
        RespondAsync(plugin => plugin.RemoveIndexerAsync(index, ct));

    private async Task<IActionResult> RespondAsync(Func<TorrentDownloaderPlugin, Task<SaveSettingsOutcome>> save)
    {
        if (pluginManager.GetPluginInstance(PluginId) is not TorrentDownloaderPlugin plugin)
        {
            return NotFound();
        }

        SaveSettingsOutcome outcome = await save(plugin);

        return outcome.Succeeded
            ? Status<object?>(null, message: outcome.Message ?? "Settings saved.")
            : Status<object?>(null, status: "error", message: outcome.Error);
    }
}
