// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader;

// The four cadences this plugin registers through IScheduledTaskPlugin.Jobs. Named once
// here so ExecuteAsync's switch, Jobs' construction and every test that asserts on a job
// name read the same constant instead of a repeated literal that could drift by typo.
public static class JobNames
{
    public const string Transfers = "transfers";
    public const string Feed = "feed";
    public const string Search = "search";
    public const string Maintenance = "maintenance";
}

// The class the host actually loads. Three contracts, one lifecycle: IPlugin's Initialize
// is synchronous with no async hook to fall back on, so it does the one thing it safely
// can - capture the context - and every real read waits for the first scheduled tick. See
// Initialize and Jobs below for why that split is deliberate rather than an oversight.
public sealed class TorrentDownloaderPlugin : IPlugin, IScheduledTaskPlugin, IUiPlugin
{
    private IPluginContext? _context;
    private SettingsGateway? _settingsGateway;
    private bool _disposed;

    // The shell's own composition root for IClock: Activator.CreateInstance (see
    // DiscoveryContractTests) requires this class to keep a public parameterless
    // constructor, so there is no constructor parameter to inject a test double through -
    // SettingsSaveHandler is where IClock actually gets exercised, and its own tests inject
    // a fake there directly instead.
    private readonly IClock _clock = new SystemClock();

    // Field-initialized rather than created in Initialize, so Dispose has something to
    // cancel and dispose even when the host disposes a plugin whose load never happened -
    // the same case the null-safe-before-Initialize contract already covers. Every tick
    // links this into the token it runs under (see ExecuteAsync), so cancelling it here is
    // what makes Dispose's "cancel in-flight work" promise real instead of aspirational.
    private readonly CancellationTokenSource _lifecycleCts = new();

    public string Name => PluginIdentity.Name;
    public string Description => PluginIdentity.Description;
    public Ulid Id => PluginIdentity.Id;
    public Version Version => PluginIdentity.Version;

    // Assigns the context and nothing else. No config read, no secret read, no I/O, no
    // network: a plugin that throws from here fails to load, and Initialize is synchronous
    // with nowhere to await a fix. Real work belongs on the first tick, in ExecuteAsync.
    public void Initialize(IPluginContext context)
    {
        _context = context;
    }

    // Every tick-time member reads through this. A tick can only happen after the host has
    // registered the plugin, so a null context here is the host calling out of order - a
    // bug worth surfacing loudly as InvalidOperationException, not a NullReferenceException
    // three frames down.
    private IPluginContext Context =>
        _context ?? throw new InvalidOperationException("the plugin was ticked before Initialize");

    private SettingsGateway SettingsGateway =>
        _settingsGateway ??= new SettingsGateway(Context.Configuration, Context.Secrets);

    // The controller's only way in: it resolves this plugin through IPluginManager (see
    // TorrentDownloaderSettingsController), which hands back the live instance rather than a
    // fresh one, and these are the three members on it a REST call is allowed to reach - one
    // per entry point the controller exposes. Each is guarded the same way GetViewAsync is -
    // a request racing Dispose gets a clean failure instead of an exception thrown into
    // ASP.NET Core's pipeline from a plugin the host is mid-teardown on.
    public Task<SaveSettingsOutcome> SaveSettingsAsync(SaveSettingsRequest request, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleGeneralAsync(request, ct));

    public Task<SaveSettingsOutcome> SaveIndexerAsync(int index, SaveSettingsRequest request, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleIndexerAsync(index, request, ct));

    public Task<SaveSettingsOutcome> SaveClientAsync(int index, SaveSettingsRequest request, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleClientAsync(index, request, ct));

    public Task<SaveSettingsOutcome> AddIndexerAsync(CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleAddIndexerAsync(ct));

    public Task<SaveSettingsOutcome> AddClientAsync(CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleAddClientAsync(ct));

    public Task<SaveSettingsOutcome> RemoveIndexerAsync(int index, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleRemoveIndexerAsync(index, ct));

    public Task<SaveSettingsOutcome> RemoveClientAsync(int index, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleRemoveClientAsync(index, ct));

    private Task<SaveSettingsOutcome> SaveAsync(Func<SettingsSaveHandler, Task<SaveSettingsOutcome>> handle)
    {
        if (_disposed)
        {
            return Task.FromResult(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));
        }

        return handle(new SettingsSaveHandler(SettingsGateway, _clock));
    }

    // The single legacy cadence a host that reads CronExpression instead of Jobs still
    // sees. Kept identical to the transfers job - the fastest of the four - so either path
    // schedules the same cadence.
    public string CronExpression => CronOrDefault(ReadSettingsOrDefault().TransfersCron, DefaultSettings.TransfersCron);

    // Read synchronously (the sync overload exists on IPluginConfiguration precisely
    // because this property cannot await) and tolerates a missing context on purpose: the
    // host may read Jobs while discovering and registering the plugin, which can happen
    // before Initialize. Returning defaults there is the useful answer - throwing would
    // fail registration outright. ExecuteAsync is a different case: it can only run after
    // registration, so a missing context there is a bug, not a discovery-time race, and
    // gets a throw instead of a default. Do not make the two symmetric.
    public IReadOnlyList<PluginScheduledJob> Jobs
    {
        get
        {
            TorrentDownloaderSettings settings = ReadSettingsOrDefault();

            return
            [
                new PluginScheduledJob(JobNames.Transfers, CronOrDefault(settings.TransfersCron, DefaultSettings.TransfersCron)),
                new PluginScheduledJob(JobNames.Feed, CronOrDefault(settings.FeedCron, DefaultSettings.FeedCron)),
                new PluginScheduledJob(JobNames.Search, CronOrDefault(settings.SearchCron, DefaultSettings.SearchCron)),
                new PluginScheduledJob(JobNames.Maintenance, CronOrDefault(settings.MaintenanceCron, DefaultSettings.MaintenanceCron)),
            ];
        }
    }

    // TorrentDownloaderSettings' own field initializers, read fresh each call rather than
    // cached, so this stays the one place the four cadence defaults are named - CronOrDefault
    // below and every caller reach through here instead of a literal that could drift from
    // the settings class.
    private static TorrentDownloaderSettings DefaultSettings => new();

    // System.Text.Json ignores nullability annotations by default, so a stored
    // {"transfersCron": null} deserializes straight past TransfersCron's non-nullable
    // declaration and its initializer, and a blank string is no more a valid cron than null
    // is. Neither should reach the host's cron parser, which this plugin does not validate
    // and is not about to start doing here - falling back to the documented default is the
    // whole fix.
    private static string CronOrDefault(string? cron, string fallback) => string.IsNullOrWhiteSpace(cron) ? fallback : cron;

    // Jobs is a property the host reads repeatedly - at registration and again on every
    // cadence change - so a read that throws here does not fail once, it fails registration
    // outright every time. IPluginConfiguration is whole-object JSON on disk: a server killed
    // mid-write or a full disk leaves truncated JSON, and Deserialize throws JsonException on
    // that. Catching broadly (short of OperationCanceledException, which is a real
    // cancellation and must propagate) and falling back to defaults is what keeps this
    // plugin's four cadences registered no matter what shape the file on disk is in. The
    // warning names the failure so the owner can see why their configured cadences were
    // ignored; it never carries the configuration's contents.
    private TorrentDownloaderSettings ReadSettingsOrDefault()
    {
        if (_context is null)
        {
            return new TorrentDownloaderSettings();
        }

        try
        {
            return _context.Configuration.GetConfiguration<TorrentDownloaderSettings>() ?? new TorrentDownloaderSettings();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            _context.Logger.LogWarning(exception, "Torrent Downloader could not read its configuration; using the default schedules.");
            return new TorrentDownloaderSettings();
        }
    }

    public Task ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(JobNames.Transfers, ct);

    // A tick arriving after Dispose is the host calling a plugin it already tore down - that
    // is a bug in the caller, not a state this method should quietly absorb, so it throws
    // rather than returning as if the tick had run. Linking the host's token with the
    // plugin's own lifecycle token means Dispose cancelling the latter reaches whichever job
    // body is currently awaiting I/O, instead of only being checked at the top of this method.
    public async Task ExecuteAsync(string jobName, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        IPluginContext context = Context;
        using CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _lifecycleCts.Token);
        linkedCts.Token.ThrowIfCancellationRequested();

        switch (jobName)
        {
            case JobNames.Transfers:
                await RunTransfersAsync(context, linkedCts.Token);
                break;
            case JobNames.Feed:
                await RunFeedAsync(context, linkedCts.Token);
                break;
            case JobNames.Search:
                await RunSearchAsync(context, linkedCts.Token);
                break;
            case JobNames.Maintenance:
                await RunMaintenanceAsync(context, linkedCts.Token);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(jobName), jobName, "Unknown job name.");
        }
    }

    // The engine holds running torrents, so the pipeline outlives a job tick and is built
    // once. Changing folders or indexers therefore lands on the next restart - rebuilding
    // an engine underneath live downloads is a good way to lose them.
    private DownloadPipeline? _pipeline;
    private readonly SemaphoreSlim _pipelineLock = new(1, 1);

    // The store outlives the pipeline and is reachable without it, because the downloads
    // page needs what the cadences recorded and must not start an engine to read it.
    // FileDownloadStore keeps its state in memory, so a second instance over the same file
    // would answer from whatever it last read - hence one field, created once, under the
    // same lock the pipeline uses.
    private IDownloadStore? _store;

    private async Task<IDownloadStore> StoreAsync(IPluginContext context, CancellationToken ct)
    {
        if (_store is not null)
            return _store;

        await _pipelineLock.WaitAsync(ct);

        try
        {
            return _store ??= new FileDownloadStore(DownloadPipeline.StorePath(context));
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private async Task<DownloadPipeline> PipelineAsync(IPluginContext context, CancellationToken ct)
    {
        if (_pipeline is not null)
            return _pipeline;

        await _pipelineLock.WaitAsync(ct);

        try
        {
            if (_pipeline is null)
            {
                LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
                await LogUngrantedHostsAsync(context, loaded.Settings, ct);

                _store ??= new FileDownloadStore(DownloadPipeline.StorePath(context));
                _pipeline = DownloadPipeline.Create(context, loaded, _store);
            }

            return _pipeline;
        }
        finally
        {
            _pipelineLock.Release();
        }
    }

    private async Task RunTransfersAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        int imported = await pipeline.Orchestrator.TransfersCycleAsync(ct);

        if (imported > 0)
            context.Logger.LogInformation("Torrent Downloader handed {Count} finished download(s) to the intake.", imported);
    }

    // The library is the list of what is watched, so refreshing it is the whole of
    // "which shows do we follow": nothing is opted into.
    private async Task RunFeedAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        int wanted = await pipeline.Orchestrator.RefreshWantedAsync(ct);

        context.Logger.LogInformation("Torrent Downloader is missing {Count} episode(s) across the library.", wanted);
    }

    private async Task RunSearchAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        int grabbed = await pipeline.Orchestrator.SearchCycleAsync(ct);

        if (grabbed > 0)
            context.Logger.LogInformation("Torrent Downloader started {Count} download(s).", grabbed);
    }

    private async Task RunMaintenanceAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        // Housekeeping is a refresh for now: it notices episodes a user filled in by hand
        // and stops wanting them, and notices files that disappeared and wants them again.
        await pipeline.Orchestrator.RefreshWantedAsync(ct);

        context.Logger.LogInformation("Torrent Downloader finished its housekeeping pass.");
    }

    private static async Task LogUngrantedHostsAsync(
        IPluginContext context,
        TorrentDownloaderSettings settings,
        CancellationToken ct
    )
    {
        HostGrants hostGrants = new(context.Grants);
        IReadOnlyList<string> ungranted = await hostGrants.EnsureAsync(settings, ct);

        if (ungranted.Count > 0)
        {
            context.Logger.LogWarning(
                "Torrent Downloader is waiting on host access for: {Hosts}",
                string.Join(", ", ungranted)
            );
        }
    }

    // One entry per plugin.json mount. ManifestTests asserts the two stay in agreement,
    // since a manifest and this property are two declarations of the same fact and nothing
    // else would catch them drifting apart - the dashboard prefers this one.
    //
    // The downloads page sits beside films and shows rather than in the admin panel: the
    // question it answers - where is episode five - is one asked while looking at the
    // library, not while configuring a server.
    public IReadOnlyList<PluginNavEntry> NavEntries { get; } =
        [
            new PluginNavEntry
            {
                Section = PluginUiSection.Settings,
                Label = PluginIdentity.Name,
                Icon = "download",
                Route = "/settings",
            },
            new PluginNavEntry
            {
                Section = PluginUiSection.Video,
                Label = "Downloads",
                Icon = "download",
                Route = "/downloads",
            },
        ];

    // A view request after Dispose is not the caller's bug the way a tick is - the host may
    // still be draining an in-flight page render while tearing the plugin down - so this
    // answers with something renderable instead of throwing into the request pipeline. No
    // I/O happens before this check, so there is nothing to cancel; the disposed case never
    // reaches the try block below.
    private static PluginView DisposedView() =>
        PluginViews.Declarative(
            PluginViews.EmptyState(
                "settings-unavailable",
                "Torrent Downloader is unavailable",
                "This plugin is disabled or is being unloaded."
            )
        );

    // These pages are the plugin's only diagnostic surface, so letting a load failure throw
    // through one hides its own cause - the owner sees a broken page instead of learning
    // their key ring rotated or their config file is truncated. Secret reads go through the
    // host's data protector, which throws CryptographicException on a rotated key ring or a
    // corrupt payload; a truncated config throws JsonException the same way Fix 1 guards
    // against on the registration path. Rendered text and the log message both name what
    // failed, never the exception detail, the settings, or a stored secret - which is also
    // why one card serves both routes: what went wrong is in the log, and guessing at it in
    // the card is how a downloads page ends up advising someone about an encryption key.
    private static PluginView PageErrorView() =>
        PluginViews.Declarative(
            PluginViews.Container(
                "page-error",
                PluginViews.Badge("page-error-badge", "Unavailable", PluginBadgeVariant.Danger),
                PluginViews.EmptyState(
                    "page-error-empty",
                    "This page could not be loaded",
                    "Check the server log for Torrent Downloader - it names what failed."
                )
            )
        );

    // The two routes this plugin mounts. A client asking for anything else is not a bug
    // worth failing the request over - the empty state is the honest answer for a route
    // this version does not have.
    public async Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        if (_disposed)
        {
            return DisposedView();
        }

        IPluginContext context = Context;

        try
        {
            return request.Route switch
            {
                "/settings" => await SettingsPageAsync(context, ct),
                "/downloads" => await DownloadsPageAsync(context, ct),
                _ => PluginViews.Declarative(PluginViews.EmptyState("unknown-route", "Nothing here")),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.Logger.LogError(exception, "Torrent Downloader could not build the view for {Route}.", request.Route);
            return PageErrorView();
        }
    }

    private async Task<PluginView> SettingsPageAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
        HostGrants hostGrants = new(context.Grants);
        IReadOnlyList<string> ungrantedHosts = await hostGrants.EnsureAsync(loaded.Settings, ct);
        IReadOnlyList<string> storedSecretKeys = await context.Secrets.KeysAsync(ct);

        return SettingsView.Build(loaded.Settings, ungrantedHosts, new HashSet<string>(storedSecretKeys, StringComparer.Ordinal));
    }

    // Reads the store and nothing else. Deliberately not through PipelineAsync: that builds
    // the engine, and someone opening a page in the dashboard should not be what starts
    // dialling peers. The store is shared with the cadences, so what this shows is what the
    // last transfers tick actually recorded rather than a second, staler copy of it.
    private async Task<PluginView> DownloadsPageAsync(IPluginContext context, CancellationToken ct)
    {
        IDownloadStore store = await StoreAsync(context, ct);

        IReadOnlyList<Transfer> transfers = await store.TransfersAsync(ct);
        IReadOnlyList<Grab> grabs = await store.ActiveGrabsAsync(ct);

        // Everything still wanted, not a page of it: the view truncates the list itself and
        // says how many it left out, which it cannot do from a list already cut short. The
        // store answers from memory, so the cost of the full list is the allocation.
        IReadOnlyList<WantedEpisode> wanted = await store.WantedAsync(int.MaxValue, ct);

        return DownloadsView.Build(transfers, grabs, wanted);
    }

    // Null-safe before Initialize (the host may dispose a plugin whose load failed) and
    // idempotent (a double dispose is not a bug worth throwing over). Cancelling before
    // flipping _disposed matters no more than the reverse here - both fields are set on the
    // same thread with nothing else observing the gap - but the order documents intent:
    // stop new work first, then reach into whatever is already running.
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifecycleCts.Cancel();
        _lifecycleCts.Dispose();
    }
}
