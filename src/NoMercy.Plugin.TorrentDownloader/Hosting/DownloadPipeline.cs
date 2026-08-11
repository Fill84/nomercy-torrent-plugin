// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.RegularExpressions;
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

        IReadOnlyList<ConfiguredIndexer> indexers = Indexers(context, loaded);

        DownloadOrchestrator orchestrator = new(
            new PluginLibraryQueryAdapter(context.Library),
            store,
            new AggregatorReleaseSearch(new IndexerAggregator([.. indexers.Select(indexer => indexer.Indexer)])),
            new ProfileReleaseChooser(ProfileFor(settings)),
            engine,
            new LibraryImportHandoff(new FinishedFolderMover(intake), context.Library, new EncodeJobDispatch(context.Services, context.Logger), context.Logger),
            new OrchestratorOptions
            {
                DownloadFolder = downloads,
                MaxConcurrentDownloads = Math.Max(1, settings.MaxConcurrentDownloads),
                IncludeSpecials = settings.IncludeSpecials,
                FollowedShowIds = settings.FollowedShowIds,
                ExtraTrackers = settings.DefaultTrackers,
            },

            PrivateTrackers(context, loaded),
            () => DateTimeOffset.UtcNow,

            // Feed indexers only: see IndexerReleaseFeed for why a query-less request must
            // never reach a Torznab endpoint.
            new IndexerReleaseFeed(new IndexerAggregator(
                [.. indexers.Where(indexer => indexer.IsFeed).Select(indexer => indexer.Indexer)])),

            FreeSpaceOn,

            // Sites only. A feed cannot resolve what a feed announced - asking SCNSRC
            // where to get the release it just named is asking the notice board for the
            // shop's address.
            new IndexerReleaseResolver(
                [.. indexers.Where(indexer => indexer.IsSite).Select(indexer => indexer.Indexer)]));

        return new DownloadPipeline(engine, orchestrator);
    }

    /// <summary>
    /// Free bytes on whichever volume holds the download folder, or null when that cannot
    /// be worked out.
    ///
    /// <para>
    /// Null rather than zero on failure, and the guard treats null as "no objection". A
    /// network share or an unusual mount that will not report its size is not a reason to
    /// stop downloading - refusing everything because a number could not be read would be
    /// a worse failure than the one the check exists to prevent.
    /// </para>
    /// </summary>
    private static long? FreeSpaceOn(string folder)
    {
        try
        {
            string? root = Path.GetPathRoot(Path.GetFullPath(folder));

            return string.IsNullOrEmpty(root) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception failure) when (failure is IOException or ArgumentException or UnauthorizedAccessException)
        {
            return null;
        }
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

    /// <summary>
    /// One configured indexer, and whether it is the kind that can be read without asking
    /// it a question. The two cadences want different subsets of the same instances - the
    /// pacer's rate limiting only works if both go through the one object per endpoint.
    /// </summary>
    private sealed record ConfiguredIndexer(PacedIndexer Indexer, bool IsFeed, bool IsSite);

    /// <summary>
    /// Shared by every site, because Cloudflare issues clearance per host and two sites on
    /// one host would otherwise solve the same gate twice.
    /// </summary>
    private static readonly ClearanceStore Clearances = new(() => DateTimeOffset.UtcNow);

    private static IReadOnlyList<ConfiguredIndexer> Indexers(IPluginContext context, LoadedSettings loaded)
    {
        List<ConfiguredIndexer> indexers = [];

        foreach (IndexerSettings settings in loaded.Settings.Indexers.Where(indexer => indexer.Enabled))
        {
            if (!Uri.TryCreate(settings.Url, UriKind.Absolute, out Uri? url))
            {
                context.Logger.LogWarning("Skipping indexer {Name}: {Url} is not a usable address.", settings.Name, settings.Url);
                continue;
            }

            string? apiKey = loaded.IndexerSecrets.FirstOrDefault(secret => secret.Name == settings.Name)?.ApiKey;

            IIndexer? indexer = Build(settings, url, apiKey, context, loaded.Settings.DefaultTrackers);

            if (indexer is null)
                continue;

            indexers.Add(new ConfiguredIndexer(new PacedIndexer(
                indexer,
                new IndexerPacer(
                    new SystemClock(),
                    TimeSpan.FromSeconds(Math.Max(1, settings.MinimumIntervalSeconds)),
                    maxConcurrency: 2,
                    failureThreshold: 3,
                    cooldown: TimeSpan.FromMinutes(15))),
                IsFeed: settings.Kind.Equals("rss", StringComparison.OrdinalIgnoreCase),
                IsSite: settings.Kind.Equals("site", StringComparison.OrdinalIgnoreCase)));
        }

        return indexers;
    }

    private static IIndexer? Build(
        IndexerSettings settings,
        Uri url,
        string? apiKey,
        IPluginContext context,
        IReadOnlyList<string> trackers)
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

            case "site" when SiteIndexer.IsUsableTemplate(settings.Url):
                return new SiteIndexer(
                    settings.Name,
                    settings.Priority,
                    settings.Url,
                    new ChallengeAwareFetch(context.HttpClient, Clearances, new BrowserIdentitySolver()),
                    trackers);

            case "site":
                // Said once here rather than failing every search: a template without the
                // placeholder searches the same page forever and looks like a dead site.
                context.Logger.LogWarning(
                    "Skipping site {Name}: its search address needs {Placeholder} where the search terms go.",
                    settings.Name,
                    SiteIndexer.QueryPlaceholder);

                return null;

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
        // One rung, not a ceiling. A ceiling reads as generous and behaves as a
        // downgrade: the 720p copy of tonight's episode is usually posted first, so it
        // becomes the answer and the owner gets a quality they did not ask for. On a real
        // server that meant 720p grabs against a 1080p setting.
        Quality = QualityLadders.Only(QualityLadders.ParseResolution(settings.MaximumResolution, Resolution.Fhd1080)),
        MinSeeders = Math.Max(1, settings.MinimumSeeders),
        AllowSeasonPacks = settings.AllowSeasonPacks,
        Codec = CodecFor(settings.Codec),

        // Set together with the codec, and the pair is what makes "h264" mean what
        // torrent-feed means by it: an untagged release is refused rather than passing as
        // "at least it is not HEVC". An untagged rip is exactly where the unwanted codec
        // hides. Naming no codec asks nothing, so the flag is irrelevant there.
        RequireCodecTag = CodecFor(settings.Codec) != VideoCodec.Unknown,

        Language = settings.EnglishOnly ? LanguageProfile.EnglishOnly : LanguageProfile.Any,

        Terms = ExcludeTerms(settings.ExcludeTerms),
    };

    /// <summary>
    /// The owner's own list of things they never want, as forbidden term rules.
    ///
    /// <para>
    /// Matched as plain text, not as a pattern: an owner typing <c>HiggsBoson</c> means
    /// that word, and the one who types <c>x265 (HEVC)</c> should not have their brackets
    /// read as a group and silently match nothing. <c>TermMatcher</c> runs a regex, so the
    /// text is escaped on the way in.
    /// </para>
    /// </summary>
    private static IReadOnlyList<TermRule> ExcludeTerms(IEnumerable<string> terms) =>
    [
        .. terms
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .Select(term => new TermRule(Regex.Escape(term.Trim()), TermKind.Forbidden, 0)),
    ];

    /// <summary>
    /// The owner's word, as the filter's own value. Anything unrecognised asks nothing
    /// rather than refusing everything, because a typo in a settings box should not silently
    /// stop every download.
    /// </summary>
    private static VideoCodec CodecFor(string? codec) => codec?.Trim().ToLowerInvariant() switch
    {
        "h264" or "x264" or "avc" => VideoCodec.H264,
        "h265" or "x265" or "hevc" => VideoCodec.H265,
        _ => VideoCodec.Unknown,
    };

    public ValueTask DisposeAsync() => _engine.DisposeAsync();
}
