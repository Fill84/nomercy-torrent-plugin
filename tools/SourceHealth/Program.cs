using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;

/// <summary>
/// Walks every enabled source through the real chain and writes down what each
/// one answered.
/// </summary>
/// <remarks>
/// The same catalogue, the same fetch, the same solver and the same readers the
/// plugin uses. A health check that asks its own questions its own way reports
/// on something nobody ships.
/// </remarks>
internal static class Program
{
    /// <summary>
    /// A real release name, because that is what the plugin asks sources for.
    /// </summary>
    /// <remarks>
    /// An indexer answers a full release name and a name database answers the
    /// show and slot inside it, so one term exercises both. It is a show that
    /// is really in the library.
    /// </remarks>
    private const string DefaultTerm = "Silo S03E06";

    private static async Task<int> Main(string[] arguments)
    {
        string term = arguments.Length > 0 ? arguments[0] : DefaultTerm;

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Information));
        ILogger logger = logging.CreateLogger("health");

        string repository = RepositoryRoot();

        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(logger).Load(
            Path.Combine(repository, "src", "NoMercy.Plugin.TorrentDownloader"));

        SourceDefinition[] enabled = [.. shipped.Where(source => source.Enabled)];

        if (enabled.Length == 0)
        {
            logger.LogError("No sources to walk. There is nothing this tool can say.");

            return 1;
        }

        // The tool is not the plugin and has no host to ask, so every host is
        // permitted here. It only ever runs when somebody typed the command.
        HostGate gate = new(TimeProvider.System);

        foreach (SourceDefinition source in enabled)
        {
            foreach (string host in source.Hosts)
            {
                gate.Configure(host, TimeSpan.FromSeconds(source.MinimumIntervalSeconds));
            }
        }

        Browser browser = new(
            new BrowserInstall(Path.Combine(repository, "_capture"), new PuppeteerBrowserDownloader(), logger),
            new HiddenStages(),
            logger);

        await using PuppeteerTabs tabs = new(browser, logger);
        BrowserSolver solver = new(tabs, logger);

        using HttpClient http = new();
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        ChallengeAwareFetch fetch = new(http, gate, new EverythingPermitted(), new ClearanceStore(), solver, solver);
        SourceCheck check = new(fetch, Readers.Shipped());

        List<SourceHealthCheck> checks = [];

        foreach (SourceDefinition source in enabled)
        {
            logger.LogInformation("Asking {Name} for '{Term}'.", source.Name, term);

            SourceHealthCheck result = await check.RunAsync(source, term, CancellationToken.None);
            checks.Add(result);

            logger.LogInformation("{Name}: {Detail}", source.Name, result.Detail);
        }

        browser.Dispose();

        string path = HealthReport.Write(
            checks,
            Path.Combine(repository, "health"),
            term,
            DateTimeOffset.UtcNow);

        logger.LogInformation(
            "{Answering} of {Total} answering. Written to {Path}.",
            checks.Count(one => !one.Flagged),
            checks.Count,
            path);

        return 0;
    }

    private static string RepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
               ?? throw new InvalidOperationException("This tool must be run from inside the repository.");
    }
}

/// <summary>
/// Every host permitted, because this tool has no host to ask.
/// </summary>
/// <remarks>
/// Only ever reached when somebody typed the command, which is the consent the
/// plugin's grants exist to obtain.
/// </remarks>
internal sealed class EverythingPermitted : IPluginGrants
{
    public Task<bool> HasAsync(string kind, string scope, CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }

    public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([]);
    }

    public Task RequestAsync(string kind, string scope, string reason, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
