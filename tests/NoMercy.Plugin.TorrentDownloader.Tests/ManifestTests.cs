using System.Text.Json;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests;

/// <summary>
/// <c>plugin.json</c> is read by the server, not by this plugin, so nothing in
/// the code fails when the two disagree. These are the checks that would
/// otherwise never happen until a server refused to load it.
/// </summary>
public class ManifestTests
{
    private static PluginManifest Manifest()
    {
        // The copy in the test's output folder, which is the copy that ships:
        // reading the one in src/ would pass on a day the file was never
        // deployed beside the assembly at all.
        string path = Path.Combine(AppContext.BaseDirectory, "plugin.json");

        Assert.True(File.Exists(path), $"No plugin.json beside the assembly at {path}.");

        PluginManifest? manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path));

        Assert.NotNull(manifest);
        return manifest;
    }

    /// <remarks>
    /// The manifest and <see cref="PluginIdentity"/> carry the version
    /// separately, and a server reporting a version it is not running is worse
    /// than a server reporting none.
    /// </remarks>
    [Fact]
    public void TheManifestsVersionIsThePluginsVersion()
    {
        Assert.Equal(PluginIdentity.Version.ToString(), Manifest().Version);
    }

    /// <remarks>
    /// The id is 0.3.4's, unchanged. It is this plugin's identity on every
    /// server that already has it, and the data folder, the grants and the
    /// settings all hang off it — a new id would install a second plugin beside
    /// the old one and inherit none of that.
    /// </remarks>
    [Fact]
    public void TheManifestKeepsTheIdFromTheVersionItReplaces()
    {
        Assert.Equal("1SBQT26FHF98EBRPYVRGD92CZF", Manifest().Id.ToString());
        Assert.Equal(PluginIdentity.Id, Manifest().Id);
    }

    /// <remarks>
    /// The server mounts what the manifest declares and asks the plugin for the
    /// route the viewer clicked. A mount with no matching nav entry is a link
    /// to a page the plugin will not serve.
    /// </remarks>
    [Fact]
    public void TheManifestsMountsAreTheNavEntries()
    {
        List<PluginUiMount> mounts = Manifest().Capabilities?.Ui?.Mounts ?? [];

        Assert.Equal(
            Pages.NavEntries.Select(entry => (entry.Section, entry.Label, entry.Icon, entry.Route)),
            mounts.Select(mount => (mount.Section, mount.Label, mount.Icon, mount.Route)));
    }

    /// <remarks>
    /// Asked of the contract this build is compiled against rather than against
    /// a number written here, because the failure being prevented is exactly
    /// the two drifting apart. The server enforces this at load and refuses the
    /// plugin outright, which is a slow way to find out.
    /// </remarks>
    [Fact]
    public void TheManifestDeclaresAnAbiTheServerAccepts()
    {
        string? targetAbi = Manifest().TargetAbi;

        Assert.False(string.IsNullOrWhiteSpace(targetAbi));
        Assert.True(
            PluginAbi.IsCompatible(targetAbi),
            $"targetAbi '{targetAbi}' is refused by a server on ABI {PluginAbi.Current}.");
    }

    /// <remarks>
    /// The server registers a plugin's cadences and its pages from the declared
    /// hooks, so an implemented interface the manifest does not mention is an
    /// interface the server never calls.
    /// </remarks>
    [Fact]
    public void TheManifestDeclaresTheHooksThePluginImplements()
    {
        List<string> hooks = Manifest().Capabilities?.Hooks ?? [];

        Assert.Contains(PluginHookCapability.ScheduledTask, hooks);
        Assert.Contains(PluginHookCapability.Ui, hooks);
    }
}
