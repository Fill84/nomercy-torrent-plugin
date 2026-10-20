using System.Text.Json;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
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
    /// <para>
    /// And so is the assembly's. Three things carry this version — the
    /// manifest, <see cref="PluginIdentity"/> and the compiled file — and the
    /// third was left at the compiler's default of 1.0.0 for the whole of
    /// 0.4.0's development.
    /// </para>
    /// <para>
    /// It matters at exactly one moment, and it is the moment nobody can afford
    /// to get wrong: a deploy onto a server that was not stopped fails and
    /// leaves the old build in place, which looks exactly like a deploy that
    /// worked. Somebody checking the file's version to tell those two apart
    /// would be reading a number that never changes.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheAssemblysVersionIsThePluginsVersionToo()
    {
        Version? compiled = typeof(PluginIdentity).Assembly.GetName().Version;

        Assert.NotNull(compiled);

        // Compared on the three parts that are written down. The fourth is the
        // revision, which nothing here sets and the compiler fills with nought.
        Assert.Equal(PluginIdentity.Version.Major, compiled!.Major);
        Assert.Equal(PluginIdentity.Version.Minor, compiled.Minor);
        Assert.Equal(PluginIdentity.Version.Build, compiled.Build);
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
        Assert.Equal(PluginIdentity.Id.ToString(), Manifest().Id.ToString());
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
    /// <para>
    /// <strong>The plugin's own page is a dashboard page.</strong> The section a
    /// mount names is what the server turns into the address the client links
    /// to, and the plugin never learns its own prefix. Mounted under
    /// <c>library</c>, the cog beside the plugin in the dashboard's own list
    /// opened it at <c>/plugins/…</c> while the title beside it went to
    /// <c>/dashboard/plugins/…</c> — two addresses for one plugin, from one
    /// row, and only one of them the page the owner was already on.
    /// </para>
    /// <para>
    /// Dashboard is also what the word means: the server's vocabulary calls it
    /// server administration beside the other owner-only panels, and reserves
    /// <c>settings</c> for where a plugin is set up rather than where it works.
    /// This plugin's eight pages are work; only one of them is setup.
    /// </para>
    /// <para>
    /// <strong>And not beside the libraries.</strong> The owner's decision of
    /// 11 September 2026: the plugin belongs in the dashboard and in the user
    /// menu, and nowhere else. It had been mounted under library as well since
    /// 2 September.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePluginsOwnPageIsMountedOnTheDashboardAndNotBesideTheLibraries()
    {
        string[] sections =
        [
            .. Pages.NavEntries
                .Where(entry => entry.Route == Pages.OverviewRoute)
                .Select(entry => entry.Section),
        ];

        // The dashboard one is what keeps the cog and the title in the
        // dashboard's own plugin list pointing at one address.
        Assert.Contains(PluginUiSection.Dashboard, sections);
        Assert.DoesNotContain(PluginUiSection.Library, Pages.NavEntries.Select(entry => entry.Section));

        // And only sections every client is expected to render: an unknown one
        // is not an error, it is quietly demoted to the add-ons page, where
        // nobody would think to look for it.
        Assert.All(
            Pages.NavEntries,
            entry => Assert.Contains(entry.Section, PluginUiSection.All));
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
    /// <para>
    /// <strong>Never lower than the newest member the plugin calls.</strong> The
    /// SDK's assemblies are the host's own copies, so a plugin built against a
    /// newer contract than the server runs loads, and then dies with a
    /// <c>MissingMethodException</c> the first time it reaches for the member the
    /// server does not have. Declared, the server refuses it at load instead,
    /// and says why.
    /// </para>
    /// <para>
    /// 12.4 is <c>IPluginStorage.MarkSparseAsync(localPath)</c> (media-server
    /// #609), which every new download calls. 12.5 is the nearest the manifest
    /// can say for <c>IPluginBrowserPage</c>'s <c>ReloadAsync</c>,
    /// <c>ContentTypeAsync</c>, <c>IsReadableAsync</c>, <c>FetchTextAsync</c> and
    /// <c>UserAgentAsync</c> (media-server #601), which arrived under 12.5 without
    /// a bump of their own and which every solve calls.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManifestAsksForEveryContractMemberThePluginCalls()
    {
        Version declared = Version.Parse(Manifest().TargetAbi!);

        Assert.True(
            declared >= new Version(12, 5),
            $"targetAbi '{declared}' lets a server without IPluginBrowserPage.FetchTextAsync (12.5) load the plugin.");
    }

    /// <remarks>
    /// <c>IPluginContext.Browser</c> opens a page only for a plugin whose manifest
    /// names <c>browser.headless</c> among its hooks and whose owner approved it
    /// (<c>PluginCapabilityBroker.Check</c>); without the line every solve is
    /// refused and every source behind a challenge is off, with a refusal that
    /// says to add exactly this line.
    /// </remarks>
    [Fact]
    public void TheManifestNamesTheBrowserCapability()
    {
        Assert.Contains(
            "browser.headless",
            Manifest().Capabilities?.Hooks ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// The host's page aborts every request to a host outside the manifest's
    /// <c>network.hosts</c> and the grants (<c>PluginBrowserPage.ConfigureAsync</c>),
    /// and a managed challenge loads its own frame and script from
    /// <c>challenges.cloudflare.com</c> (the captured interstitial's CSP names it).
    /// Without that host no challenge can clear: the page sits on "Just a
    /// moment…" for the whole solve, and every gated source is lost.
    /// </remarks>
    [Fact]
    public void TheManifestNamesTheHostAChallengeIsClearedThrough()
    {
        Assert.Contains(
            "challenges.cloudflare.com",
            Manifest().Capabilities?.Network?.Hosts ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// <para>
    /// <strong>The encoder is a hook the manifest has to name.</strong> On
    /// contract 12 the server hands a plugin <c>IPluginContext.Encoder</c> — and
    /// with it Jobs, which says what became of an encode — only when the
    /// manifest declares the encoder hook. Left out, every download would stage
    /// and sit in the intake folder for an encode that could never be asked for,
    /// and the only sign would be one warning in the log.
    /// </para>
    /// <para>
    /// Asked of the contract's own constant rather than the word written here,
    /// because the server compares the manifest against that constant.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManifestNamesTheEncoderHook()
    {
        Assert.Contains(
            PluginHookCapability.Encoder,
            Manifest().Capabilities?.Hooks ?? [],
            StringComparer.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// <para>
    /// <strong>Library write is a hook the manifest has to name.</strong> The
    /// server hands a plugin <c>IPluginContext.LibraryWriter</c> only when the
    /// manifest declares <c>libraryWrite</c> and the owner granted it a library
    /// (<c>PluginContextFactory</c>), and the writer's recycle area is where the
    /// encode of an episode the plugin delivered from a release no name source
    /// names goes once its replacement is staged — the owner's rule of
    /// 15 October 2026. Left out, every replacement would wait in the intake
    /// folder for ever.
    /// </para>
    /// <para>
    /// It is elevated, so naming it widens the manifest and the server loads the
    /// update disabled until the owner approves it once more.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheManifestNamesTheLibraryWriteHook()
    {
        Assert.Contains(
            PluginHookCapability.LibraryWrite,
            Manifest().Capabilities?.Hooks ?? [],
            StringComparer.OrdinalIgnoreCase);
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
