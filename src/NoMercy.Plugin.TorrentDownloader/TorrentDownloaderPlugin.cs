// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
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

    // Field-initialized rather than created in Initialize, so Dispose has something to
    // cancel and dispose even when the host disposes a plugin whose load never happened -
    // the same case the null-safe-before-Initialize contract already covers. Every tick
    // links this into the token it runs under (see ExecuteAsync), so cancelling it here is
    // what makes Dispose's "cancel in-flight work" promise real instead of aspirational.
    private readonly CancellationTokenSource _lifecycleCts = new();

    public string Name => PluginIdentity.Name;
    public string Description => PluginIdentity.Description;
    public Guid Id => PluginIdentity.Id;
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
    // fresh one, and this is the one member on it a REST call is allowed to reach. Guarded the
    // same way GetViewAsync is - a request racing Dispose gets a clean failure instead of an
    // exception thrown into ASP.NET Core's pipeline from a plugin the host is mid-teardown on.
    public Task<SaveSettingsOutcome> SaveSettingsAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        if (_disposed)
        {
            return Task.FromResult(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));
        }

        return new SettingsSaveHandler(SettingsGateway).HandleAsync(request, ct);
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

    // Every job body below has the same shape for this stage: find out what the owner has
    // not yet granted and say so, then log what the job would actually do. No orchestration
    // - Stage 4 replaces each body with the real cycle, not this structure.
    private async Task RunTransfersAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
        await LogUngrantedHostsAsync(context, loaded.Settings, ct);
        context.Logger.LogInformation("Torrent Downloader would poll configured clients for transfer progress.");
    }

    private async Task RunFeedAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
        await LogUngrantedHostsAsync(context, loaded.Settings, ct);
        context.Logger.LogInformation("Torrent Downloader would poll configured indexer feeds for new releases.");
    }

    private async Task RunSearchAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
        await LogUngrantedHostsAsync(context, loaded.Settings, ct);
        context.Logger.LogInformation("Torrent Downloader would search configured indexers for missing episodes.");
    }

    private async Task RunMaintenanceAsync(IPluginContext context, CancellationToken ct)
    {
        LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
        await LogUngrantedHostsAsync(context, loaded.Settings, ct);
        context.Logger.LogInformation("Torrent Downloader would run housekeeping on completed and stalled transfers.");
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

    // One entry, matching plugin.json's own mount. ManifestTests asserts the two stay in
    // agreement, since a manifest and this property are two declarations of the same fact
    // and nothing else would catch them drifting apart.
    public IReadOnlyList<PluginNavEntry> NavEntries { get; } =
        [
            new PluginNavEntry
            {
                Section = PluginUiSection.Settings,
                Label = PluginIdentity.Name,
                Icon = "download",
                Route = "/settings",
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

    // This page is the plugin's only diagnostic surface, so letting a load failure throw
    // through it hides its own cause - the owner sees a broken settings page instead of
    // learning their key ring rotated or their config file is truncated. Secret reads go
    // through the host's data protector, which throws CryptographicException on a rotated
    // key ring or a corrupt payload; a truncated config throws JsonException the same way
    // Fix 1 guards against on the registration path. Rendered text and the log message both
    // name what failed, never the exception detail, the settings, or a stored secret.
    private static PluginView SettingsErrorView() =>
        PluginViews.Declarative(
            PluginViews.Container(
                "settings-error",
                PluginViews.Badge("settings-error-badge", "Unavailable", PluginBadgeVariant.Danger),
                PluginViews.EmptyState(
                    "settings-error-empty",
                    "Settings could not be loaded",
                    "Check the server log for Torrent Downloader, and confirm its encryption key has not changed."
                )
            )
        );

    // The one route this stage has. A client asking for anything else is not a bug worth
    // failing the request over - the empty state is the honest answer for a route this
    // version does not have.
    public async Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        if (_disposed)
        {
            return DisposedView();
        }

        if (request.Route != "/settings")
        {
            return PluginViews.Declarative(PluginViews.EmptyState("settings-unknown-route", "Nothing here"));
        }

        IPluginContext context = Context;

        try
        {
            LoadedSettings loaded = await SettingsGateway.LoadAsync(ct);
            HostGrants hostGrants = new(context.Grants);
            IReadOnlyList<string> ungrantedHosts = await hostGrants.EnsureAsync(loaded.Settings, ct);
            IReadOnlyList<string> storedSecretKeys = await context.Secrets.KeysAsync(ct);

            return SettingsView.Build(loaded.Settings, ungrantedHosts, new HashSet<string>(storedSecretKeys, StringComparer.Ordinal));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            context.Logger.LogError(exception, "Torrent Downloader could not build its settings view.");
            return SettingsErrorView();
        }
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
