using System.Net;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Solver;

/// <summary>
/// Where the host has no browser to offer, the sources behind a challenge are off and the owner is told
/// why, in the host's words (<c>docs/07-solver.md</c> § Where the browser is off).
/// </summary>
/// <remarks>
/// The browser is the host's since <c>S16-06</c>: whether there is one — a Chrome installed, an <c>Xvfb</c>
/// on Linux, the capability approved — is the host's to say, and the plugin only carries the sentence.
/// </remarks>
public sealed class TheBrowserIsOffWhenTheHostHasNoneTests : IDisposable
{
    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    private const string Reason = "Chrome is not installed on this host.";

    private const string Why = "Chrome is not installed on this host, so sources behind a challenge are off.";

    [Fact]
    public async Task TheSolverDoesNotRepeatTheWarningForEveryHost()
    {
        CapturingLogger log = new();
        BrowserSolver solver = new(new FakeTabs { HasBrowser = false }, log);

        await solver.SolveAsync(new("https://predb.me/"), CancellationToken.None);
        await solver.SolveAsync(new("https://1337x.to/"), CancellationToken.None);

        Assert.DoesNotContain(log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task WithNoBrowserTheChallengesBeforeARunAreFoundAndNotClearedAndThePassSaysWhy()
    {
        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(new CapturingLogger()).Load();
        Settings settings = new();

        foreach (SourceDefinition other in shipped.Where(one => one.Name != "1337x"))
        {
            settings.DisabledDefaultSources.Add(other.Name);
        }

        SourceDefinition gated = shipped.Single(one => one.Name == "1337x");
        Uri address = new((gated.SearchAddress ?? gated.Url).Replace("{query}", "a", StringComparison.Ordinal));

        FakeHttp http = new FakeHttp().Answers(
            HttpStatusCode.Forbidden,
            Fixture("cloudflare-interstitial-1337x.html"),
            (CloudflareChallenge.MitigatedHeader, "challenge"));

        FakeGrants grants = new();
        grants.Grant(address.Host);

        ActivityJournal journal = new();
        FakeBrowser browser = new() { Reason = Reason };

        await using Chain chain = new(
            new FakePluginContext { DataFolderPath = _folder, Permits = grants, Hidden = browser },
            journal,
            shipped,
            http);

        await chain.ClearTheWayAsync(settings, CancellationToken.None);

        IReadOnlyList<ActivityEvent> clearance =
            [.. journal.Snapshot().History.Where(one => one.Stage == ActivityStage.Clearance)];

        Assert.Single(http.Attempts);
        Assert.Contains(clearance, one => one.Detail == Why);
        Assert.DoesNotContain(clearance, one => (one.Detail ?? string.Empty).Contains("cleared", StringComparison.Ordinal));
        Assert.Empty(browser.Opened);
    }

    [Fact]
    public async Task TheSourcesPageSaysWhichSourcesAreOffWithNoBrowserAndWhy()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder, Hidden = new FakeBrowser { Reason = Reason } });

        PluginView view = await plugin.GetViewAsync(Requests.View(Pages.SourcesRoute), CancellationToken.None);

        string words = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains($"{Why} Off:", words, StringComparison.Ordinal);
        Assert.Contains("1337x", words, StringComparison.Ordinal);
    }

    /// <remarks>
    /// A host that wired no browser at all, or an owner who has not approved <c>browser.headless</c>: the
    /// facade refuses by name, and that refusal is what the page says.
    /// </remarks>
    [Fact]
    public async Task TheSourcesPageCarriesTheHostsRefusalWhereTheFacadeIsNotThere()
    {
        using TorrentDownloaderPlugin plugin = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder });

        PluginView view = await plugin.GetViewAsync(Requests.View(Pages.SourcesRoute), CancellationToken.None);

        string words = string.Join(" ", Rendered.EveryValue(view));

        Assert.Contains("IPluginContext.Browser", words, StringComparison.Ordinal);
        Assert.Contains(HostTabs.Consequence, words, StringComparison.Ordinal);
    }

    /// <remarks>A host with a browser has nothing to put above the table.</remarks>
    [Fact]
    public async Task TheSourcesPageSaysNothingAboutTheBrowserWhereTheHostHasOne()
    {
        using TorrentDownloaderPlugin plugin = new();
        FakeBrowser browser = new();

        plugin.Initialize(new FakePluginContext { DataFolderPath = _folder, Hidden = browser });

        PluginView view = await plugin.GetViewAsync(Requests.View(Pages.SourcesRoute), CancellationToken.None);

        string words = string.Join(" ", Rendered.EveryValue(view));

        Assert.DoesNotContain(HostTabs.Consequence, words, StringComparison.Ordinal);

        // Drawing the page asked the host whether it has a browser, and opened nothing: a page is a Chrome.
        Assert.Equal(0, browser.Opens);
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_folder);
    }
}
