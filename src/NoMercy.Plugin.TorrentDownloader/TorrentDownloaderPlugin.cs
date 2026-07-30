// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Hosting;
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

    // The single legacy cadence a host that reads CronExpression instead of Jobs still
    // sees. Kept identical to the transfers job - the fastest of the four - so either path
    // schedules the same cadence.
    public string CronExpression => ReadSettingsOrDefault().TransfersCron;

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
                new PluginScheduledJob(JobNames.Transfers, settings.TransfersCron),
                new PluginScheduledJob(JobNames.Feed, settings.FeedCron),
                new PluginScheduledJob(JobNames.Search, settings.SearchCron),
                new PluginScheduledJob(JobNames.Maintenance, settings.MaintenanceCron),
            ];
        }
    }

    private TorrentDownloaderSettings ReadSettingsOrDefault() =>
        _context?.Configuration.GetConfiguration<TorrentDownloaderSettings>() ?? new TorrentDownloaderSettings();

    public Task ExecuteAsync(CancellationToken ct = default) => ExecuteAsync(JobNames.Transfers, ct);

    public async Task ExecuteAsync(string jobName, CancellationToken ct = default)
    {
        IPluginContext context = Context;
        ct.ThrowIfCancellationRequested();

        switch (jobName)
        {
            case JobNames.Transfers:
                await RunTransfersAsync(context, ct);
                break;
            case JobNames.Feed:
                await RunFeedAsync(context, ct);
                break;
            case JobNames.Search:
                await RunSearchAsync(context, ct);
                break;
            case JobNames.Maintenance:
                await RunMaintenanceAsync(context, ct);
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

    // Task 6 replaces this call with the real settings tree; the seam is this one
    // delegation point so that swap touches only the body, not GetViewAsync's signature.
    public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        return Task.FromResult(new PluginView());
    }

    // Null-safe before Initialize (the host may dispose a plugin whose load failed) and
    // idempotent (a double dispose is not a bug worth throwing over).
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
    }
}
