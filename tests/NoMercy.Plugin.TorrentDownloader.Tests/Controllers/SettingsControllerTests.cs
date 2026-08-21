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
        SettingsController controller = For(Initialised());

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
        SettingsController controller = For(plugin);

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
    /// <para>
    /// <strong>F1.</strong> 0.3.4 awaited the cycle inside the HTTP request, so
    /// it ran on the caller's cancellation token: a browser tab closed after
    /// half an hour threw away twenty-nine minutes of work. The cycle belongs
    /// to the plugin and the request only starts it.
    /// </para>
    /// <para>
    /// Proved with a token that was cancelled before the request was even made.
    /// The cycle still runs, and the line it writes when there is nothing
    /// configured is what says it got that far.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task RunStartsACycleThatDoesNotBelongToTheCaller()
    {
        FakePluginContext context = new();
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(context);

        using CancellationTokenSource gone = new();

        await gone.CancelAsync();

        SettingsController controller = For(plugin);

        OkObjectResult result = Assert.IsType<OkObjectResult>(controller.Run(gone.Token));
        PluginStatusResponse<bool> response = Assert.IsType<PluginStatusResponse<bool>>(result.Value);

        Assert.Equal("started", response.Status);
        Assert.True(response.Data);

        await Until(() => context.Log.Lines.Any(line => line.Contains("No folders", StringComparison.Ordinal)));
    }

    /// <remarks>
    /// Stopping what is not running says so rather than answering "ok". An
    /// endpoint that accepts a request it did not carry out has the page show
    /// something that did not happen.
    /// </remarks>
    [Fact]
    public void StoppingWhenNothingIsRunningSaysSo()
    {
        SettingsController controller = For(Initialised());

        OkObjectResult result = Assert.IsType<OkObjectResult>(controller.Stop());
        PluginStatusResponse<bool> response = Assert.IsType<PluginStatusResponse<bool>>(result.Value);

        Assert.Equal("idle", response.Status);
        Assert.False(response.Data);
        Assert.Contains("nothing", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Waits for something the plugin does on its own.</summary>
    private static async Task Until(Func<bool> what)
    {
        using CancellationTokenSource giving = new(TimeSpan.FromSeconds(20));

        while (!what())
        {
            giving.Token.ThrowIfCancellationRequested();

            await Task.Delay(TimeSpan.FromMilliseconds(10), giving.Token);
        }
    }

    /// <remarks>
    /// The page posts what its fields hold — flat names and values — and this
    /// is the only way the owner can change a setting from the dashboard. Until
    /// 21 August 2026 the page rendered every setting as text and offered no
    /// way to change any of them.
    /// </remarks>
    [Fact]
    public async Task ThePageCanChangeOneSettingWithoutSendingTheRest()
    {
        TorrentDownloaderPlugin plugin = Initialised();
        SettingsController controller = For(plugin);

        await controller.Save(Writable(), CancellationToken.None);

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Edit(
                new Dictionary<string, object?> { ["client.listenPort"] = 6881 },
                CancellationToken.None));

        Assert.Equal("ok", Assert.IsType<PluginStatusResponse<SaveResult>>(result.Value).Status);

        Settings saved = await plugin.Settings.LoadAsync(CancellationToken.None);

        Assert.Equal(6881, saved.Client.ListenPort);

        // The folders were never in the post and are still there. A page that
        // sent one section and cleared the others would lose a setting every
        // time the owner saved a different one.
        Assert.Equal(Path.GetTempPath(), saved.IncompleteFolder);
    }

    /// <remarks>
    /// The store is what validates, for the page exactly as for the endpoint
    /// beside it, and its reason is what the owner reads.
    /// </remarks>
    [Fact]
    public async Task AnEditTheStoreRefusesIsRefusedWithItsReason()
    {
        SettingsController controller = For(Initialised());

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Edit(
                new Dictionary<string, object?> { ["cadences.feed"] = "nonsense" },
                CancellationToken.None));

        PluginStatusResponse<SaveResult> response =
            Assert.IsType<PluginStatusResponse<SaveResult>>(result.Value);

        Assert.Equal("refused", response.Status);
        Assert.Contains("feed", response.Message ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// A field nothing answers to is refused by name rather than dropped: the
    /// owner typed into it and watched it save.
    /// </remarks>
    [Fact]
    public async Task AFieldThatIsNotASettingIsRefusedByName()
    {
        SettingsController controller = For(Initialised());

        OkObjectResult result = Assert.IsType<OkObjectResult>(
            await controller.Edit(
                new Dictionary<string, object?> { ["client.listenPortt"] = 6881 },
                CancellationToken.None));

        PluginStatusResponse<SaveResult> response =
            Assert.IsType<PluginStatusResponse<SaveResult>>(result.Value);

        Assert.Equal("refused", response.Status);
        Assert.Contains("client.listenPortt", response.Message ?? string.Empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// The controller as the host builds one: from the server's container, on a
    /// request, on the route that says which plugin was asked for.
    /// </summary>
    private static SettingsController For(TorrentDownloaderPlugin plugin)
    {
        return new SettingsController(new LoadedPlugins(plugin)).On(plugin.Id);
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
