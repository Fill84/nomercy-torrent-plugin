using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Storage;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// The plugin the server loads: its identity, its four cadences and its pages.
/// </summary>
public sealed class TorrentDownloaderPlugin : IPlugin, IScheduledTaskPlugin, IUiPlugin, IPluginServiceRegistrator
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ActivityJournal _journal = new();
    private readonly SemaphoreSlim _migrating = new(1, 1);
    private Database? _database;
    private EpisodeRepository? _episodes;
    private GrabRepository? _grabs;
    private bool _migrated;
    private IPluginContext? _context;
    private SettingsStore? _settings;
    private LiveSnapshot? _live;
    private Chain? _chain;
    private BittorrentEngine? _engine;
    private HttpClient? _trackerHttp;
    private Transfers? _transfers;
    private SourceLedgerRepository? _ledger;
    private IReadOnlyList<SourceDefinition>? _shipped;

    /// <summary>What the last search cycle decided, for the pages that say so.</summary>
    private CycleReport? _lastCycle;
    private DateTimeOffset? _lastCycleAt;
    private int _running;
    private int _unconfigured;
    private int _announced;
    private bool _disposed;

    /// <summary>
    /// Where every stage says what it is doing, and what the dashboard renders.
    /// </summary>
    public IActivityJournal Journal => _journal;

    /// <summary>
    /// The settings, and the only door to them.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Before <see cref="Initialize"/>, because there is no host to read them
    /// from — an empty store handed out instead would answer with defaults the
    /// owner never chose and would be believed.
    /// </exception>
    public SettingsStore Settings => _settings
        ?? throw new InvalidOperationException("The plugin has not been initialised, so it has no settings yet.");

    public string Name => PluginIdentity.Name;

    public string Description => PluginIdentity.Description;

    public Ulid Id => PluginIdentity.Id;

    public Version Version => PluginIdentity.Version;

    /// <summary>
    /// Cancelled on <see cref="Dispose"/>. Everything long-running the plugin
    /// starts in a later slice — the torrent engine, the solver's browser, the
    /// journal writer — stops on this, so none of it outlives the plugin inside
    /// a server that believes it is gone.
    /// </summary>
    public CancellationToken Lifetime => _lifetime.Token;

    /// <summary>
    /// Ignored by a server that understands <see cref="Jobs"/>, which registers
    /// each of them separately. It names the fastest cadence so that a host
    /// with only the single slot still ticks the work that cannot wait.
    /// </summary>
    public string CronExpression => JobNames.TransfersCron;

    /// <summary>
    /// All four, from the first version, though only some of them do anything
    /// yet.
    /// </summary>
    /// <remarks>
    /// Cadences are registered once, when the server starts. A job added to
    /// this list in a later slice does not begin ticking when it is
    /// implemented; it waits for the next restart. Declaring all four now costs
    /// nothing and saves a restart nobody would connect to the cause.
    /// The expressions are the defaults from docs/04-domain.md § Settings;
    /// S0-05 lets the owner change them.
    /// </remarks>
    public IReadOnlyList<PluginScheduledJob> Jobs { get; } =
    [
        new(JobNames.Transfers, JobNames.TransfersCron),
        new(JobNames.Feed, JobNames.FeedCron),
        new(JobNames.Search, JobNames.SearchCron),
        new(JobNames.Maintenance, JobNames.MaintenanceCron),
    ];

    public IReadOnlyList<PluginNavEntry> NavEntries => Pages.NavEntries;

    /// <summary>
    /// Every page, not only the two in navigation: Shows and Queue are reached
    /// from the dashboard rather than from a sidebar.
    /// </summary>
    public PluginRouteTable Routes => Pages.Routes;

    /// <summary>
    /// Stores the context and does nothing else.
    /// </summary>
    /// <remarks>
    /// No I/O: this runs while the server is still coming up, so anything slow
    /// here delays it and anything that throws takes the plugin out before it
    /// has a page on which to say why.
    /// </remarks>
    public void Initialize(IPluginContext context)
    {
        _context = context;

        // Objects, not I/O: nothing here opens a file, a socket or a database.
        // The settings are read when something asks for them, and the database
        // is created and migrated the first time it is really used.
        _settings = new(context.Configuration, context.Secrets);
        _database = new(context.DataFolderPath);
        _episodes = new(_database);
        _grabs = new(_database);
        _ledger = new(_database);
        _live = new(context.Hub, _journal, context.Logger, CurrentCycle);
    }

    /// <summary>The database, migrated up to date before anything opens it.</summary>
    private async Task<Database> DatabaseAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _database ?? throw new InvalidOperationException("The plugin has not been initialised.");
    }

    /// <summary>
    /// The episode store, migrated up to date before it is first handed out.
    /// </summary>
    /// <remarks>
    /// Migrating on first use rather than during <c>Initialize</c>, which does
    /// no I/O. Behind a semaphore because a cadence tick and a page render can
    /// arrive at once on a plugin that has only just loaded, and two threads
    /// running <c>001-initial.sql</c> together would have one of them fail on a
    /// table the other had just created.
    /// </remarks>
    public async Task<EpisodeRepository> EpisodesAsync(CancellationToken ct)
    {
        if (_episodes is null || _database is null)
        {
            throw new InvalidOperationException("The plugin has not been initialised, so it has no store yet.");
        }

        if (_migrated)
        {
            return _episodes;
        }

        await _migrating.WaitAsync(ct);

        try
        {
            if (!_migrated)
            {
                await _database.MigrateAsync(ct);
                _migrated = true;
            }
        }
        finally
        {
            _migrating.Release();
        }

        return _episodes;
    }

    /// <summary>
    /// The grab store, over the same migrated database as the episodes.
    /// </summary>
    /// <remarks>
    /// Through <see cref="EpisodesAsync"/> rather than beside it, so there is
    /// one migration and one place that decides when it has run.
    /// </remarks>
    public async Task<GrabRepository> GrabsAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _grabs ?? throw new InvalidOperationException("The plugin has not been initialised, so it has no store yet.");
    }

    /// <summary>
    /// Registers the plugin itself, so its controllers can be handed the one
    /// instance the host loaded rather than construct a second with no context.
    /// </summary>
    public void RegisterServices(IServiceCollection services)
    {
        services.AddSingleton(this);
    }

    /// <summary>
    /// The entry point of a host that registered <see cref="CronExpression"/>
    /// rather than <see cref="Jobs"/>, so it runs what that expression names.
    /// </summary>
    public Task ExecuteAsync(CancellationToken ct = default)
    {
        return ExecuteAsync(JobNames.Transfers, ct);
    }

    public async Task ExecuteAsync(string jobName, CancellationToken ct = default)
    {
        if (!JobNames.All.Contains(jobName))
        {
            // Rather than shrug: the server only ever passes back a name it was
            // given, so an unknown one means its list and this one have drifted,
            // and a job that quietly did nothing would hide that for as long as
            // it kept ticking.
            throw new ArgumentOutOfRangeException(
                nameof(jobName),
                jobName,
                $"No such job. This plugin has {string.Join(", ", JobNames.All)}.");
        }

        AnnounceOnce();

        // The plugin's own lifetime, never the caller's: a cycle belongs to the
        // plugin and a cadence tick that returns must not take it down with it.
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);

        switch (jobName)
        {
            case JobNames.Feed:
                await HarvestAsync(work.Token);
                break;

            case JobNames.Search:
                await SearchAsync(work.Token);
                break;

            case JobNames.Transfers:
                await TransfersAsync(work.Token);
                break;

            // Maintenance is S8-04's: a library refresh on a cadence, which
            // nothing yet drives.
            default:
                break;
        }
    }

    /// <summary>
    /// One pass over everything the torrent client is holding.
    /// </summary>
    /// <remarks>
    /// The fastest cadence, because a completion nobody notices is an episode
    /// nobody gets. It runs on a plugin that has folders and does nothing at
    /// all on one that does not: there is nowhere to stage to, so noticing a
    /// completion could only end in a file thrown away.
    /// </remarks>
    private async Task TransfersAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return;
        }

        BittorrentEngine engine = await EngineAsync(settings, ct);

        _transfers ??= new(
            engine,
            await GrabsAsync(ct),
            new HostLibrary(Context.Library),
            new Stager(_journal, Context.Logger),
            new EncodeDispatch(Context.Services, _journal, Context.Logger),
            _journal,
            Context.Logger);

        await _transfers.TickAsync(settings.IncompleteFolder, settings.IntakeFolder, ct);
    }

    /// <summary>Reads every feed into the name pool.</summary>
    private async Task HarvestAsync(CancellationToken ct)
    {
        if (await ChainAsync(ct) is not (Chain chain, Settings settings))
        {
            return;
        }

        await chain.Harvest(settings).RunAsync(ct);
    }

    /// <summary>
    /// Looks for every missing episode and takes what the profile accepts.
    /// </summary>
    /// <remarks>
    /// The report is kept so the pages can say what this cycle decided about
    /// each episode. Nothing is written to the store here: recording a grab is
    /// <c>S6-01</c>, and a decision the plugin cannot yet act on is not a fact
    /// about an episode.
    /// </remarks>
    private async Task SearchAsync(CancellationToken ct)
    {
        if (await ChainAsync(ct) is not (Chain chain, Settings settings))
        {
            return;
        }

        IReadOnlyList<TrackedEpisode> tracked = await Tracked(ct);
        GrabRepository grabs = await GrabsAsync(ct);

        Interlocked.Increment(ref _running);

        try
        {
            _lastCycle = await chain.Search(settings).RunAsync(
                tracked,
                new(
                    settings.Profile,

                    // The hashes a download has already failed on. Without it
                    // the next cycle chooses the same release and fails the
                    // same way, for as long as the plugin runs.
                    await grabs.BlacklistedAsync(ct),
                    settings.DryRun,
                    settings.IncompleteFolder)
                {
                    DefaultTrackers = settings.Client.DefaultTrackers,
                },
                ct);
        }
        finally
        {
            Interlocked.Decrement(ref _running);
            _lastCycleAt = DateTimeOffset.UtcNow;
        }

        // After the cycle rather than during it: a grab is written down once
        // the client has been handed something, and the pages that say what
        // happened read the store rather than anything held in memory.
        await CycleRecord.WriteAsync(_lastCycle, tracked, grabs, DateTimeOffset.UtcNow, ct);
    }

    /// <summary>What the last cycle decided about each episode it looked at.</summary>
    /// <remarks>
    /// Held rather than stored: a decision the plugin cannot yet act on is not
    /// a fact about an episode, and writing one would have the pages state it
    /// as though it were. Recording a real grab is <c>S6-01</c>.
    /// </remarks>
    public IReadOnlyList<EpisodeOutcome> LastCycle => _lastCycle?.Outcomes ?? [];

    /// <summary>
    /// The one chain, built on first use and kept.
    /// </summary>
    /// <remarks>
    /// Not in <c>Initialize</c>, which does no I/O: this reads the settings,
    /// the catalogue beside the assembly and asks the server for grants. Behind
    /// the same semaphore as the migration because two cadences can tick at
    /// once on a plugin that has just loaded.
    /// </remarks>
    private async Task<(Chain Chain, Settings Settings)?> ChainAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return null;
        }

        BittorrentEngine engine = await EngineAsync(settings, ct);

        await _migrating.WaitAsync(ct);

        try
        {
            _chain ??= new(
                Context,
                _journal,
                new NamePoolRepository(await DatabaseAsync(ct)),
                Shipped(),
                engine: engine,
                ledger: await LedgerAsync(ct));
        }
        finally
        {
            _migrating.Release();
        }

        await _chain.PrepareAsync(settings, ct);

        return (_chain, settings);
    }

    /// <summary>
    /// The settings, when the plugin has enough of them to do anything.
    /// </summary>
    /// <remarks>
    /// A plugin nobody has configured does nothing at all, and says so once. It
    /// has nowhere to put a download, so searching for one would spend every
    /// site's patience on a file that could only be thrown away — and the owner
    /// would see activity and no results.
    /// </remarks>
    private async Task<Settings?> ConfiguredAsync(CancellationToken ct)
    {
        Settings settings = await Settings.LoadAsync(ct);

        if (settings.IncompleteFolder.Length != 0 && settings.IntakeFolder.Length != 0)
        {
            return settings;
        }

        SayOnce(ref _unconfigured, "No folders are configured, so nothing is searched for. Set them in Settings.");

        return null;
    }

    /// <summary>
    /// The torrent client, started once.
    /// </summary>
    /// <remarks>
    /// One for the process, whatever ticks in between: the client owns sockets
    /// and a port mapping, and a second would bind a port the first already has
    /// and report it as somebody else's. Behind the same semaphore as the
    /// migration, because two cadences can tick at once on a plugin that has
    /// only just loaded.
    /// </remarks>
    private async Task<BittorrentEngine> EngineAsync(Settings settings, CancellationToken ct)
    {
        await _migrating.WaitAsync(ct);

        try
        {
            if (_engine is null)
            {
                // The trackers get an HttpClient of their own, not the one the
                // sites use: that one carries a browser's user agent because
                // half the indexers challenge anything else, and a tracker has
                // no such quarrel.
                _trackerHttp ??= new();

                _engine = new(
                    settings.Client.ListenPort,
                    TimeSpan.FromMinutes(settings.Client.MetadataTimeoutMinutes),
                    _journal,
                    Context.Logger,
                    new SocketTrackerTransport(_trackerHttp),
                    new SocketPeerDialler(),
                    resume: new ResumeKeeper(
                        settings.IncompleteFolder,
                        TimeSpan.FromSeconds(settings.Client.ResumeIntervalSeconds),
                        TimeProvider.System));

                _engine.Start();
            }

            return _engine;
        }
        finally
        {
            _migrating.Release();
        }
    }

    /// <summary>The host, or a failure that says the plugin was never initialised.</summary>
    private IPluginContext Context => _context
        ?? throw new InvalidOperationException("The plugin has not been initialised.");

    public async Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        // Rendered per request from the current state, never from a tree held
        // between requests: a cached page goes stale silently, and the page
        // most worth trusting is the one saying what is happening now.
        switch (request.Route)
        {
            case Pages.SettingsRoute:
                // Names, never values. The page is given the keys that exist
                // and has no route to what is behind them.
                return SettingsView.Render(
                    await Settings.LoadAsync(ct),
                    await Settings.SecretsSetAsync(ct),
                    []);

            case Pages.ShowsRoute:
                return ShowsView.Render(ShowSummaries.Summarise(await Tracked(ct)));

            case Pages.QueueRoute:
                return QueueView.Render(await Tracked(ct));

            case Pages.DownloadsRoute:
                return DownloadsView.Render(await DownloadRowsAsync(ct));

            case Pages.SourcesRoute:
                return SourcesView.Render(await SourceReportsAsync(ct), DateTimeOffset.UtcNow);

            case Pages.SkippedRoute:
                return SkippedView.Render(await (await GrabsAsync(ct)).SkippedAsync(ct));

            case Pages.HistoryRoute:
                return HistoryView.Render(
                    [.. (await (await GrabsAsync(ct)).HistoryAsync(ct)).Select(Line)]);

            default:
                return DashboardView.Render(_journal.Snapshot(), CurrentCycle());
        }
    }

    private async Task<IReadOnlyList<TrackedEpisode>> Tracked(CancellationToken ct)
    {
        return await (await EpisodesAsync(ct)).AllAsync(ct);
    }

    /// <summary>
    /// One row per grab, with what the client says about it beside it.
    /// </summary>
    /// <remarks>
    /// <strong>G4.</strong> The rows are the grabs and the transfer is what may
    /// be missing, never the other way round: 0.3.4 built this page from the
    /// client's list, so a grab the client had not taken up was on no page at
    /// all while its episode showed as unavailable.
    /// </remarks>
    private async Task<IReadOnlyList<DownloadRow>> DownloadRowsAsync(CancellationToken ct)
    {
        Settings settings = await Settings.LoadAsync(ct);
        IReadOnlyList<StoredDownload> grabbed = await (await GrabsAsync(ct)).OpenAsync(ct);

        Dictionary<string, TorrentStatus> byHash = new(StringComparer.OrdinalIgnoreCase);

        foreach (TorrentStatus status in await RunningAsync(ct))
        {
            byHash[status.InfoHash] = status;
        }

        return
        [
            .. grabbed.Select(one => new DownloadRow(
                one,
                byHash.GetValueOrDefault(one.InfoHash),
                settings.IncompleteFolder)),
        ];
    }

    /// <summary>
    /// Every source in the catalogue, with what it last answered.
    /// </summary>
    /// <remarks>
    /// Every source, not only the ones that have answered: a site nobody has
    /// asked is missing from the ledger, and a page built from the ledger alone
    /// would leave it off entirely rather than saying it has never been asked.
    /// </remarks>
    private async Task<IReadOnlyList<SourceReport>> SourceReportsAsync(CancellationToken ct)
    {
        Settings settings = await Settings.LoadAsync(ct);
        IReadOnlyDictionary<string, SourceAnswer> answers = await (await LedgerAsync(ct)).AllAsync(ct);

        List<SourceReport> reports = [];

        foreach (SourceDefinition source in Chain.Catalogue(Shipped(), settings).Enabled)
        {
            if (!answers.TryGetValue(source.Name, out SourceAnswer? answer))
            {
                reports.Add(new(source.Name, null, 0, null, TimeSpan.Zero, null));

                continue;
            }

            reports.Add(new(
                source.Name,
                answer.At,
                answer.Rows,
                answer.Refusal,
                answer.Duration,

                // The gate's current pace, which a refusal has widened. The
                // configured figure would say a rate-limited site is askable
                // now, which is exactly the confusion this column exists to end.
                answer.At + (_chain?.IntervalFor(source) ?? TimeSpan.FromSeconds(source.MinimumIntervalSeconds))));
        }

        return reports;
    }

    /// <summary>The ledger, over the same migrated database as everything else.</summary>
    private async Task<SourceLedgerRepository> LedgerAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _ledger ?? throw new InvalidOperationException("The plugin has not been initialised, so it has no store yet.");
    }

    /// <summary>
    /// The sources that ship, read once.
    /// </summary>
    /// <remarks>
    /// A file beside the assembly that cannot change while the server runs, so
    /// a page render must not pay to read it again.
    /// </remarks>
    private IReadOnlyList<SourceDefinition> Shipped()
    {
        return _shipped ??= new CatalogueLoader(
            (_context ?? throw new InvalidOperationException("The plugin has not been initialised.")).Logger).Load();
    }

    /// <summary>
    /// One stored history row as the page reads it.
    /// </summary>
    /// <remarks>
    /// The subject is the show and the slot when the line is about an episode,
    /// and the release when it is not — a line reading only "dispatched" is
    /// exactly the entry an owner opens this page for and learns nothing from.
    /// </remarks>
    private static HistoryLine Line(HistoryRow row)
    {
        string subject = row is { ShowTitle: string show, Season: int season, Number: int number }
            ? $"{show} S{season:00}E{number:00}"
            : row.ReleaseTitle ?? row.Event;

        return new(row.Event, row.At, subject, row.Detail);
    }

    /// <summary>
    /// What the torrent client says it is holding, or nothing when there is no
    /// client.
    /// </summary>
    /// <remarks>
    /// Nothing is not the same as nought transfers, and the page treats it as
    /// such: a grab with no transfer beside it says the client has not taken it
    /// up rather than drawing a torrent stuck at nought per cent.
    /// </remarks>
    private Task<IReadOnlyList<TorrentStatus>> RunningAsync(CancellationToken ct)
    {
        return _engine is null
            ? Task.FromResult<IReadOnlyList<TorrentStatus>>([])
            : _engine.StatusAsync(ct);
    }

    /// <summary>
    /// What the status bar says about the search cycle.
    /// </summary>
    /// <remarks>
    /// When a cycle has run, when it finished; before then, null — which the
    /// page draws as "never run" rather than as nought. The time of the next
    /// one stays unknown: a cadence's schedule belongs to the host that
    /// registered it, and this plugin is never told it.
    /// </remarks>
    private CycleStatus CurrentCycle()
    {
        return new(_running > 0, _lastCycleAt, null);
    }

    public void Dispose()
    {
        // Guarded rather than relying on the source: cancelling or reading the
        // token of a disposed CancellationTokenSource throws, so a second
        // dispose — which a host is entitled to do — would take the shutdown
        // path down with it.
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();

        // Before the chain, because the client holds the listening sockets and
        // the port mapping: a plugin the server believes is gone must not still
        // be answering peers.
        _engine?.Dispose();
        _trackerHttp?.Dispose();

        // Before the token source goes: the chain owns a browser and a desktop,
        // and a Chrome that outlives the plugin is one nobody can see to close.
        _chain?.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _lifetime.Dispose();

        // After the token, so anything stopping on it that publishes a last
        // change still has somewhere to publish it.
        _live?.Dispose();
    }

    /// <summary>
    /// Says once, in the server's own log, which version is running.
    /// </summary>
    /// <remarks>
    /// Once, not once per tick: transfers ticks every minute and a line a
    /// minute is a line nobody reads. It answers the question a deploy leaves
    /// open — a plugin's assembly is held open by a running server, so a copy
    /// onto one that was not stopped fails and the old build stays, which looks
    /// exactly like a deploy that worked (docs/01-plugin.md § Deploying).
    /// </remarks>
    /// <summary>
    /// Says something once, however many times a cadence ticks.
    /// </summary>
    /// <remarks>
    /// Transfers alone ticks every minute, and a line a minute is a line
    /// nobody reads — which is how a message that mattered went unnoticed in
    /// 0.3.4's log.
    /// </remarks>
    private void SayOnce(ref int said, string message)
    {
        if (Interlocked.Exchange(ref said, 1) == 0)
        {
            _context?.Logger.LogInformation("{Message}", message);
        }
    }

    private void AnnounceOnce()
    {
        if (Interlocked.Exchange(ref _announced, 1) != 0)
        {
            return;
        }

        _context?.Logger.LogInformation(
            "{Name} {Version} awake.",
            PluginIdentity.Name,
            PluginIdentity.Version);
    }
}
