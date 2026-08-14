using Microsoft.AspNetCore.Mvc;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Mvc;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

public class SettingsControllerTests
{
    /// <remarks>
    /// The endpoint and the page are two ways into one save, so the endpoint
    /// refuses exactly what the page refuses — a rule enforced in only one of
    /// them is a rule the other way round it.
    /// </remarks>
    [Fact]
    public async Task TheEndpointRefusesWhatTheStoreRefuses()
    {
        SettingsController controller = new(Initialised());

        Settings settings = Writable();
        settings.Cadences.Feed = "nonsense";

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Save(settings, CancellationToken.None));
        PluginStatusResponse<SaveResult> response =
            Assert.IsType<PluginStatusResponse<SaveResult>>(result.Value);

        Assert.Equal("refused", response.Status);
        Assert.False(response.Data?.Saved);
        Assert.Contains("feed", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A secret goes in and never comes back out: there is no endpoint that
    /// reads one, so nothing outside the secret store is ever offered a value.
    /// </remarks>
    [Fact]
    public async Task ASecretCanBeSetAndTheSettingsResponseOnlyNamesIt()
    {
        TorrentDownloaderPlugin plugin = Initialised();
        SettingsController controller = new(plugin);

        await controller.SetSecret(
            new(SettingsStore.IndexerApiKey("own-1"), "hunter2"),
            CancellationToken.None);

        OkObjectResult result = Assert.IsType<OkObjectResult>(await controller.Get(CancellationToken.None));
        PluginDataResponse<SettingsResponse> response =
            Assert.IsType<PluginDataResponse<SettingsResponse>>(result.Value);

        Assert.Contains(SettingsStore.IndexerApiKey("own-1"), response.Data!.SecretsSet);
        Assert.DoesNotContain(
            "hunter2",
            System.Text.Json.JsonSerializer.Serialize(response.Data),
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// Not "ok". An endpoint that accepts a request it cannot carry out is
    /// worse than one that refuses: the page shows a cycle beginning, nothing
    /// happens, and nothing anywhere says why.
    /// </remarks>
    [Theory]
    [InlineData("run")]
    [InlineData("stop")]
    public void RunAndStopSayThatNothingRunsYet(string which)
    {
        SettingsController controller = new(Initialised());

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            which == "run" ? controller.Run() : controller.Stop());
        PluginStatusResponse<bool> response = Assert.IsType<PluginStatusResponse<bool>>(result.Value);

        Assert.Equal("not-ready", response.Status);
        Assert.False(response.Data);
        Assert.Contains("nothing", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    private static TorrentDownloaderPlugin Initialised()
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());
        return plugin;
    }

    private static Settings Writable()
    {
        return new()
        {
            IncompleteFolder = Path.GetTempPath(),
            IntakeFolder = Path.GetTempPath(),
        };
    }
}
