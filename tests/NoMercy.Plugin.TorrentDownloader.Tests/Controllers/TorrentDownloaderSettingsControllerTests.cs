// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Mvc;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

public class TorrentDownloaderSettingsControllerTests
{
    // A raw unit test, not an integration test through ASP.NET Core's routing pipeline:
    // PluginRouteConvention and PluginControllerCapabilityFilter both live in the host
    // (NoMercy.Api), outside this repo, so what is exercised here is exactly what this
    // plugin owns - the controller resolving the live plugin through IPluginManager and
    // translating its outcome, given PluginId the way the host's convention supplies it.
    private static TorrentDownloaderSettingsController BuildController(FakePluginManager pluginManager)
    {
        RouteData routeData = new();
        routeData.Values["pluginId"] = PluginIdentity.Id.ToString();

        return new TorrentDownloaderSettingsController(pluginManager)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext(),
                RouteData = routeData,
            },
        };
    }

    [Fact]
    public async Task SaveSettings_ReachesTheLivePluginThroughIPluginManagerAndSaves()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings();
        plugin.Initialize(context);
        FakePluginManager pluginManager = new() { Instance = plugin, InstanceId = PluginIdentity.Id };
        TorrentDownloaderSettingsController controller = BuildController(pluginManager);

        IActionResult result = await controller.SaveSettings(
            new SaveSettingsRequest
            {
                TransfersCron = "*/3 * * * *",
                FeedCron = "*/15 * * * *",
                SearchCron = "0 */6 * * *",
                MaintenanceCron = "0 4 * * *",
            },
            CancellationToken.None
        );

        result.Should().BeOfType<OkObjectResult>();
        TorrentDownloaderSettings saved = (TorrentDownloaderSettings)context.Configuration.Stored!;
        saved.TransfersCron.Should().Be("*/3 * * * *");
    }

    [Fact]
    public async Task SaveSettings_WhenThePluginIdDoesNotResolveToThisPlugin_ReturnsNotFound()
    {
        FakePluginManager pluginManager = new() { Instance = null, InstanceId = PluginIdentity.Id };
        TorrentDownloaderSettingsController controller = BuildController(pluginManager);

        IActionResult result = await controller.SaveSettings(new SaveSettingsRequest(), CancellationToken.None);

        result.Should().BeOfType<NotFoundResult>();
    }

    [Fact]
    public async Task SaveSettings_WhenValidationFails_ReturnsAnErrorStatusWithoutPersisting()
    {
        TorrentDownloaderPlugin plugin = new();
        FakePluginContext context = new();
        context.Configuration.Stored = new TorrentDownloaderSettings();
        plugin.Initialize(context);
        FakePluginManager pluginManager = new() { Instance = plugin, InstanceId = PluginIdentity.Id };
        TorrentDownloaderSettingsController controller = BuildController(pluginManager);
        int savesBefore = context.Configuration.SavedObjects.Count;

        IActionResult result = await controller.SaveSettings(
            new SaveSettingsRequest { TransfersCron = "   " },
            CancellationToken.None
        );

        OkObjectResult ok = result.Should().BeOfType<OkObjectResult>().Which;
        PluginStatusResponse<object?> status = ok.Value.Should().BeOfType<PluginStatusResponse<object?>>().Which;
        status.Status.Should().Be("error");
        status.Message.Should().NotBeNullOrWhiteSpace();
        context.Configuration.SavedObjects.Should().HaveCount(savesBefore);
    }
}
