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
// choose. [HttpPost(SettingsView.SaveSettingsMethod)] is the other half of the agreement:
// SettingsView's forms all call CallPlugin(SettingsView.SaveSettingsMethod, ...), so the
// same constant is what makes that method string resolve to this action instead of two
// literals that could quietly drift apart.
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
    public async Task<IActionResult> SaveSettings([FromBody] SaveSettingsRequest request, CancellationToken ct)
    {
        if (pluginManager.GetPluginInstance(PluginId) is not TorrentDownloaderPlugin plugin)
        {
            return NotFound();
        }

        SaveSettingsOutcome outcome = await plugin.SaveSettingsAsync(request, ct);

        return outcome.Succeeded
            ? Status<object?>(null, message: "Settings saved.")
            : Status<object?>(null, status: "error", message: outcome.Error);
    }
}
