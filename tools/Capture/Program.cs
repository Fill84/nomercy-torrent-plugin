using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tools.Capture;

/// <summary>
/// Saves the page a source really answers into <c>tests/fixtures/</c>.
/// </summary>
/// <remarks>
/// Through the same catalogue, the same fetch and the same solver the plugin
/// uses. A capture taken with a browser by hand, or with curl, is a page the
/// plugin has never seen — and every reader fault in
/// <c>docs/10-known-failures.md</c> § E is a reader that matched something the
/// page did not contain.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] arguments)
    {
        if (arguments.Length == 0)
        {
            Console.Error.WriteLine(
                """
                Capture <source name> [<search term>]

                  Saves what a source answers into tests/fixtures/. The source name is
                  the one in sources.json, quoted if it has a space.

                  Capture "LimeTorrents" "Silo S03E06"
                """);

            return 1;
        }

        string wanted = arguments[0];
        string term = arguments.Length > 1 ? arguments[1] : "Silo S03E06";

        using ILoggerFactory logging = LoggerFactory.Create(builder => builder
            .AddSimpleConsole(console => console.SingleLine = true)
            .SetMinimumLevel(LogLevel.Debug));
        ILogger logger = logging.CreateLogger("capture");

        string repository = RepositoryRoot();
        string fixtures = Path.Combine(repository, "tests", "fixtures");
        Directory.CreateDirectory(fixtures);

        IReadOnlyList<SourceDefinition> shipped = new CatalogueLoader(logger).Load(
            Path.Combine(repository, "src", "NoMercy.Plugin.TorrentDownloader"));

        SourceDefinition? source = shipped.FirstOrDefault(
            candidate => string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase));

        if (source is null)
        {
            logger.LogError(
                "No source called '{Name}'. The catalogue has: {Names}.",
                wanted,
                string.Join(", ", shipped.Select(candidate => candidate.Name)));

            return 1;
        }

        // A feed takes no question and is read whole, so its own address is the
        // capture. Refusing would leave the sources that answer "what came out
        // recently" with no fixture at all.
        Uri address = source.SearchAddress is null
            ? new(source.Url)
            : new(Query.Write(source.SearchAddress, term, source.Query));

        // The tool is not the plugin and has no host to ask, so every host is
        // permitted here. The plugin's own grants are the server's business;
        // this only ever runs when somebody typed the command.
        HostGate gate = new(TimeProvider.System);
        gate.Configure(address.Host, TimeSpan.FromSeconds(source.MinimumIntervalSeconds));

        string dataFolder = Path.Combine(repository, "_capture");
        Browser browser = new(
            new BrowserInstall(dataFolder, new PuppeteerBrowserDownloader(), logger),
            new HiddenStages(),
            logger);

        await using PuppeteerTabs tabs = new(browser, logger);
        BrowserSolver solver = new(tabs, logger);

        using HttpClient http = new();
        http.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        ChallengeAwareFetch fetch = new(http, gate, new EverythingPermitted(), new ClearanceStore(), solver, solver);

        logger.LogInformation("Asking {Name} for '{Term}' at {Address}.", source.Name, term, address);

        FetchResult result = await fetch.GetAsync(address, source.SearchGated || source.Gated, CancellationToken.None);

        if (!result.Ok)
        {
            logger.LogError("{Name} did not answer: {Reason}", source.Name, result.Failure);

            browser.Dispose();

            return 1;
        }

        // Named for what it is: a JSON body saved as .html is a fixture nobody
        // can open without wondering.
        string extension = result.Body!.TrimStart().StartsWith('{') || result.Body.TrimStart().StartsWith('[')
            ? "json"
            : result.Body.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
              || result.Body.Contains("<rss", StringComparison.OrdinalIgnoreCase)
                ? "xml"
                : "html";

        string path = Path.Combine(fixtures, $"{Slug(source.Name)}.{extension}");
        await File.WriteAllTextAsync(path, result.Body!, CancellationToken.None);

        logger.LogInformation(
            "Wrote {Bytes} bytes to {Path}. Read it before writing a reader against it.",
            result.Body!.Length,
            path);

        browser.Dispose();

        return 0;
    }

    /// <summary>A file name that is the source's name and nothing clever.</summary>
    private static string Slug(string name)
    {
        return new string([.. name.ToLowerInvariant().Select(character => char.IsAsciiLetterOrDigit(character) ? character : '-')]);
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
