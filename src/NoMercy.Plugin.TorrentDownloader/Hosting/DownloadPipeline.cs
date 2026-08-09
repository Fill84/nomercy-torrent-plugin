// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Adapters;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Profiles;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Everything the plugin needs to actually download something, assembled once.
///
/// <para>
/// The engine holds running torrents, so it has to outlive a single job tick - which
/// is why this is built once and kept rather than composed per cadence. Changing the
/// folders or the indexers therefore takes effect on the next server restart, and that
/// is a deliberate simplification: rebuilding the engine underneath live downloads is
/// a good way to lose them.
/// </para>
/// </summary>
internal sealed class DownloadPipeline : IAsyncDisposable
{
    private readonly TorrentEngine _engine;

    private DownloadPipeline(TorrentEngine engine, DownloadOrchestrator orchestrator)
    {
        _engine = engine;
        Orchestrator = orchestrator;
    }

    public DownloadOrchestrator Orchestrator { get; }

    /// <summary>
    /// Where the store's one file lives.
    ///
    /// <para>
    /// Named here rather than at each caller because FileDownloadStore holds its state in
    /// memory: two instances over the same path are two answers to the same question, and
    /// the one that did not write last is wrong. The downloads page and the four cadences
    /// therefore share a single instance, which the plugin owns and passes in.
    /// </para>
    /// </summary>
    public static string StorePath(IPluginContext context) => Path.Combine(context.DataFolderPath, "downloads.json");

    public static DownloadPipeline Create(IPluginContext context, LoadedSettings loaded, IDownloadStore store)
    {
        TorrentDownloaderSettings settings = loaded.Settings;

        string downloads = string.IsNullOrWhiteSpace(settings.IncompleteFolder)
            ? Path.Combine(context.DataFolderPath, "incomplete")
            : settings.IncompleteFolder;

        string intake = string.IsNullOrWhiteSpace(settings.IntakeFolder)
            ? Path.Combine(context.DataFolderPath, "intake")
            : settings.IntakeFolder;

        TorrentEngine engine = new(
            [new HttpTracker(context.HttpClient), new UdpTracker(new SocketUdpTransport())],
            new TcpPeerDialer(),
            new HttpTorrentFileFetcher(context.HttpClient),
            new TorrentEngineOptions
            {
                DownloadFolder = downloads,

                // Beside the plugin's own data, not beside the media: a user clearing out
                // downloads should not take the resume records with it.
                StateFolder = Path.Combine(context.DataFolderPath, "resume"),
            },
            () => DateTimeOffset.UtcNow);

        DownloadOrchestrator orchestrator = new(
            new PluginLibraryQueryAdapter(context.Library),
            store,
            new AggregatorReleaseSearch(new IndexerAggregator(Indexers(context, loaded))),
            new ProfileReleaseChooser(DefaultProfile),
            engine,
            new IntakeHandoff(intake),
            new OrchestratorOptions { DownloadFolder = downloads, IncludeSpecials = settings.IncludeSpecials },

            // No private trackers in the settings shape yet, so everything is public and
            // nothing can upload. That is the safe direction for this default to fail in.
            new PrivateTrackerRegistry([]),
            () => DateTimeOffset.UtcNow);

        return new DownloadPipeline(engine, orchestrator);
    }

    private static IReadOnlyList<PacedIndexer> Indexers(IPluginContext context, LoadedSettings loaded)
    {
        List<PacedIndexer> indexers = [];

        foreach (IndexerSettings settings in loaded.Settings.Indexers.Where(indexer => indexer.Enabled))
        {
            if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out Uri? url))
            {
                context.Logger.LogWarning("Skipping indexer {Name}: {Url} is not a usable address.", settings.Name, settings.Url);
                continue;
            }

            string? apiKey = loaded.IndexerSecrets.FirstOrDefault(secret => secret.Name == settings.Name)?.ApiKey;

            IIndexer? indexer = Build(settings, url, apiKey, context);

            if (indexer is null)
                continue;

            indexers.Add(new PacedIndexer(
                indexer,
                new IndexerPacer(
                    new SystemClock(),
                    TimeSpan.FromSeconds(Math.Max(1, settings.MinimumIntervalSeconds)),
                    maxConcurrency: 2,
                    failureThreshold: 3,
                    cooldown: TimeSpan.FromMinutes(15))));
        }

        return indexers;
    }

    private static IIndexer? Build(IndexerSettings settings, Uri url, string? apiKey, IPluginContext context)
    {
        switch (settings.Kind.ToLowerInvariant())
        {
            case "torznab" when !string.IsNullOrWhiteSpace(apiKey):
                return new TorznabIndexer(
                    settings.Name,
                    settings.Priority,
                    url,
                    apiKey,
                    context.HttpClient,
                    [.. settings.Categories.Select(category => int.TryParse(category, out int number) ? number : -1).Where(number => number > 0)]);

            case "torznab":
                // A Torznab endpoint without a key answers every search with an error, so
                // saying why once beats a failure per cycle that names nothing.
                context.Logger.LogWarning("Skipping Torznab indexer {Name}: it has no API key.", settings.Name);
                return null;

            case "rss":
                return new RssIndexer(settings.Name, settings.Priority, url, context.HttpClient, settings.Categories);

            default:
                context.Logger.LogWarning("Skipping indexer {Name}: '{Kind}' is not a kind this plugin knows.", settings.Name, settings.Kind);
                return null;
        }
    }

    /// <summary>
    /// Until profiles are a settings section, one sensible ladder.
    ///
    /// <para>
    /// Season packs are on now that a grab owns every episode it settles. They were off
    /// because a pack satisfying twelve episodes left eleven still wanted and grabbed
    /// again as eleven more torrents; that is fixed, and the orchestrator will still only
    /// consider a pack once enough of the season is missing to be worth its bytes.
    /// </para>
    /// </summary>
    private static ReleaseProfile DefaultProfile { get; } = new()
    {
        Name = "default",
        Quality = new QualityLadder(
            [
                new QualityDefinition("WEB-720p", Resolution.Hd720, ReleaseSource.Unknown),
                new QualityDefinition("WEB-1080p", Resolution.Fhd1080, ReleaseSource.Unknown),
            ],
            "WEB-1080p"),
        MinSeeders = 2,
        AllowSeasonPacks = true,
    };

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
