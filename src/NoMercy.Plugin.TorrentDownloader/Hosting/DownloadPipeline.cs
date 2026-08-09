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
            ? Path.Combine(context.DataFolderPath, "finished")
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
            new ProfileReleaseChooser(ProfileFor(settings)),
            engine,
            new LibraryImportHandoff(new FinishedFolderMover(intake), context.Library, context.EventBus, context.Logger),
            new OrchestratorOptions
            {
                DownloadFolder = downloads,
                IncludeSpecials = settings.IncludeSpecials,
                FollowedShowIds = settings.FollowedShowIds,
            },

            PrivateTrackers(context, loaded),
            () => DateTimeOffset.UtcNow);

        return new DownloadPipeline(engine, orchestrator);
    }

    /// <summary>
    /// The trackers the owner added on purpose, and the only reason anything ever uploads.
    ///
    /// <para>
    /// A malformed announce URL is dropped with a warning rather than thrown: the registry
    /// refuses one it cannot parse, and letting that throw here would take the whole
    /// pipeline down - which means no downloads at all because one entry has a typo in a
    /// field nobody can see on the page. Dropping it fails the safe way instead: that
    /// tracker's torrents are treated as public, and public never seeds.
    /// </para>
    /// </summary>
    private static PrivateTrackerRegistry PrivateTrackers(IPluginContext context, LoadedSettings loaded)
    {
        List<PrivateTracker> trackers = [];

        foreach (PrivateTrackerSettings settings in loaded.Settings.PrivateTrackers.Where(tracker => tracker.Enabled))
        {
            PrivateTrackerSecret? secret = loaded.PrivateTrackerSecrets.FirstOrDefault(entry => entry.Name == settings.Name);

            if (secret is null)
            {
                context.Logger.LogWarning("Skipping private tracker {Name}: it has no announce URL saved.", settings.Name);
                continue;
            }

            if (!Uri.TryCreate(secret.AnnounceUrl, UriKind.Absolute, out _))
            {
                // The URL itself is never logged: it carries the passkey.
                context.Logger.LogWarning("Skipping private tracker {Name}: its announce URL cannot be read.", settings.Name);
                continue;
            }

            trackers.Add(new PrivateTracker
            {
                Name = settings.Name,
                AnnounceUrl = secret.AnnounceUrl,
                ApiKey = secret.ApiKey,
                Seed = settings.Seed,
                SeedRatioTarget = settings.SeedRatioTarget,
                SeedTimeTarget = TimeSpan.FromHours(Math.Max(0, settings.SeedTimeTargetHours)),
            });
        }

        return new PrivateTrackerRegistry(trackers);
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
    /// The owner's three answers, as the profile the decider wants.
    ///
    /// <para>
    /// Everything else on <see cref="ReleaseProfile"/> - codecs, blocked groups, term
    /// rules, size bounds - stays at its default until somebody asks for it. A knob
    /// nobody understands is a knob that gets set wrong and blamed on the plugin.
    /// </para>
    /// </summary>
    private static ReleaseProfile ProfileFor(TorrentDownloaderSettings settings) => new()
    {
        Name = "default",
        Quality = QualityLadders.UpTo(QualityLadders.ParseResolution(settings.MaximumResolution, Resolution.Fhd1080)),
        MinSeeders = Math.Max(1, settings.MinimumSeeders),
        AllowSeasonPacks = settings.AllowSeasonPacks,
    };

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
