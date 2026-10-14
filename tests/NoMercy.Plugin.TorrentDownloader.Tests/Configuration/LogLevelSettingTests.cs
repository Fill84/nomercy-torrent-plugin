using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Mvc;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Configuration;

/// <summary>The log level the owner sets on the Settings page. GitHub issue #7.</summary>
public class LogLevelSettingTests
{
    /// <remarks>
    /// Information until the owner says otherwise, which is what the server's own log already gets, and the
    /// five levels the issue names on the page under Advanced — with the folder the file is in written under
    /// it, so the owner who turns on Debug knows where to look.
    /// </remarks>
    [Fact]
    public void TheLevelIsInformationUntilChosenAndThePageOffersTheFive()
    {
        Assert.Equal(LogLevel.Information, new Settings().LogLevel);

        string folder = Path.Combine("data", "plugin", "logs");

        PluginView view = SettingsView.Render(new Settings(), [], [], advanced: true, logFolder: folder);

        PluginFormField field = Field(view, "logLevel");

        Assert.Equal("Information", field.Value);
        Assert.Equal(["Error", "Warning", "Information", "Debug", "Trace"], field.Options.Select(option => option.Value));
        Assert.Contains(folder, string.Join(" ", Rendered.Words(view)), StringComparison.Ordinal);

        // Advanced, so an owner changing a folder does not walk past it.
        PluginView plain = SettingsView.Render(new Settings(), [], [], advanced: false, logFolder: folder);

        Assert.DoesNotContain(Fields(plain), one => one.Name == "logLevel");
    }

    /// <remarks>
    /// A save on the Settings page changes the level of the very next line, with no restart: an owner chasing
    /// a fault turns Debug on and watches, and a level that waited for a restart would have them restart the
    /// server — and lose the state they were chasing.
    /// </remarks>
    [Fact]
    public async Task ALevelSavedOnThePageAppliesToTheNextLine()
    {
        FakePluginContext context = new();
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(context);

        SettingsController controller = new SettingsController(new LoadedPlugins(plugin)).On(plugin.Id);

        await controller.Save(Writable(), CancellationToken.None);

        OkObjectResult saved = Assert.IsType<OkObjectResult>(await controller.Edit(
            new Dictionary<string, object?> { ["logLevel"] = "Debug" },
            CancellationToken.None));

        Assert.Equal("ok", Assert.IsType<PluginStatusResponse<SaveResult>>(saved.Value).Status);

        plugin.Log.LogDebug("seen at Debug");

        await controller.Edit(new Dictionary<string, object?> { ["logLevel"] = "Warning" }, CancellationToken.None);

        plugin.Log.LogInformation("not seen at Warning");
        plugin.Log.Flush();

        string file = string.Join(
            "\n",
            Directory.Exists(plugin.Log.Folder)
                ? Directory.GetFiles(plugin.Log.Folder).Select(File.ReadAllText)
                : []);

        Assert.StartsWith(context.DataFolderPath, plugin.Log.Folder, StringComparison.Ordinal);
        Assert.Contains("seen at Debug", file, StringComparison.Ordinal);
        Assert.DoesNotContain("not seen at Warning", file, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The JSON endpoint is the other way into the same save, and it can carry a level the page never offers.
    /// Critical and None are refused rather than stored: None would make the plugin silent everywhere,
    /// including its errors.
    /// </remarks>
    [Theory]
    [InlineData(LogLevel.Critical)]
    [InlineData(LogLevel.None)]
    public async Task ALevelThePageDoesNotOfferIsRefused(LogLevel level)
    {
        TorrentDownloaderPlugin plugin = new();
        plugin.Initialize(new FakePluginContext());

        Settings settings = Writable();
        settings.LogLevel = level;

        SaveResult result = await plugin.Settings.SaveAsync(settings, CancellationToken.None);

        Assert.False(result.Saved);
        Assert.Contains("log level", result.Said() ?? string.Empty, StringComparison.OrdinalIgnoreCase);

        // And through the page, by name.
        Settings edited = new();

        Assert.NotEmpty(SettingsEdit.Apply(edited, new Dictionary<string, string?> { ["logLevel"] = level.ToString() }));
        Assert.Equal(LogLevel.Information, edited.LogLevel);
    }

    private static IReadOnlyList<PluginFormField> Fields(PluginView view)
    {
        return Rendered.ById(view, SettingsView.FormId).Props.GetValueOrDefault("fields") is IEnumerable<PluginFormField> fields
            ? [.. fields]
            : [];
    }

    private static PluginFormField Field(PluginView view, string name)
    {
        return Assert.Single(Fields(view), one => one.Name == name);
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
