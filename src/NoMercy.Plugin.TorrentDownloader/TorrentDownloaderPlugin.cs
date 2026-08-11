// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Adapters;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
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

    public Task<SaveSettingsOutcome> SavePrivateTrackerAsync(int index, SaveSettingsRequest request, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandlePrivateTrackerAsync(index, request, ct));

    public Task<SaveSettingsOutcome> AddPrivateTrackerAsync(CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleAddPrivateTrackerAsync(ct));

    public Task<SaveSettingsOutcome> RemovePrivateTrackerAsync(int index, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleRemovePrivateTrackerAsync(index, ct));

    public Task<SaveSettingsOutcome> FollowShowAsync(int showId, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleFollowShowAsync(showId, ct));

    public Task<SaveSettingsOutcome> UnfollowShowAsync(int showId, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleUnfollowShowAsync(showId, ct));

    /// <summary>
    /// Follows a show the owner named, rather than one they clicked.
    ///
    /// <para>
    /// The only way to reach a show no page lists - which, since the refresh holds only
    /// shows with an episode on the server that are still going out, is most of a library.
    /// The library itself is asked every time; nothing about the shows this can reach is
    /// stored, because storing them is exactly the list an owner does not want to look at.
    /// </para>
    ///
    /// <para>
    /// The library is asked directly rather than through the pipeline: naming a show should
    /// not be what starts an engine dialling peers.
    /// </para>
    /// </summary>
    public async Task<SaveSettingsOutcome> FollowByNameAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        if (_disposed || _context is null)
            return Remember(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));

        if (string.IsNullOrWhiteSpace(request.Name))
            return Remember(SaveSettingsOutcome.Failure("Type the name of a show first."));

        try
        {
            IReadOnlyList<LibraryShow> shows =
                await new PluginLibraryQueryAdapter(_context.Library).GetShowsAsync(ct);

            ShowMatch match = LibraryShowFinder.Find(shows, request.Name);

            return match switch
            {
                { Outcome: ShowLookup.One, Show: { } show } => Remember(
                    await SaveAsync(handler => handler.HandleFollowShowAsync(show.ShowId, ct)) is { Succeeded: true }
                        ? SaveSettingsOutcome.Done($"Following {show.Title}. The next refresh picks up what is missing.")
                        : SaveSettingsOutcome.Failure($"{show.Title} could not be followed. The server log says why.")),

                { Outcome: ShowLookup.Several } => Remember(SaveSettingsOutcome.Failure(
                    $"More than one show matches that: {string.Join(", ", match.Candidates)}. Type the year in brackets too.")),

                _ => Remember(SaveSettingsOutcome.Failure(
                    $"Nothing in your library is called \"{request.Name.Trim()}\". Add it to the library first.")),
            };
        }
        catch (Exception failure)
        {
            _context.Logger.LogWarning(failure, "Torrent Downloader could not follow a show by name.");

            return Remember(SaveSettingsOutcome.Failure("That show could not be followed. The server log says why."));
        }
    }

    public Task<SaveSettingsOutcome> PauseDownloadAsync(string infoHash, CancellationToken ct = default) =>
        OnDownloadAsync(orchestrator => orchestrator.PauseDownloadAsync(infoHash, ct), "Paused.", ct);

    public Task<SaveSettingsOutcome> ResumeDownloadAsync(string infoHash, CancellationToken ct = default) =>
        OnDownloadAsync(orchestrator => orchestrator.ResumeDownloadAsync(infoHash, ct), "Resumed.", ct);

    public Task<SaveSettingsOutcome> CancelDownloadAsync(string infoHash, CancellationToken ct = default) =>
        OnDownloadAsync(
            orchestrator => orchestrator.CancelDownloadAsync(infoHash, ct),
            "Cancelled. The episode is back on the queue and this release is skipped for now.",
            ct);

    public Task<SaveSettingsOutcome> SearchNowAsync(int showId, int season, int episode, CancellationToken ct = default) =>
        OnDownloadAsync(
            orchestrator => orchestrator.SearchNowAsync(new EpisodeKey(showId, season, episode), ct),
            "Grabbed it.",
            ct,
            nothingHappened: "Nothing usable was found for that one just now.");

    public async Task<SaveSettingsOutcome> AddTorrentAsync(SaveSettingsRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Source))
            return Remember(SaveSettingsOutcome.Failure("Paste a magnet link first."));

        if (_disposed || _context is null)
            return Remember(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));

        try
        {
            DownloadPipeline pipeline = await PipelineAsync(_context, ct);
            ManualAdd added = await pipeline.Orchestrator.AddManuallyAsync(request.Source, ct);

            return Remember(added.Added ? SaveSettingsOutcome.Done(added.Message) : SaveSettingsOutcome.Failure(added.Message));
        }
        catch (Exception failure)
        {
            _context.Logger.LogWarning(failure, "Torrent Downloader could not add a link by hand.");

            return Remember(SaveSettingsOutcome.Failure("That link could not be added. The server log says why."));
        }
    }

    /// <summary>
    /// Lifts a skip, so the release can be chosen again.
    ///
    /// <para>
    /// Straight to the store rather than through the pipeline: nothing is running that
    /// needs telling, and a page that only wants to forget something should not be what
    /// starts an engine.
    /// </para>
    /// </summary>
    public async Task<SaveSettingsOutcome> AllowReleaseAsync(string handle, CancellationToken ct = default)
    {
        if (_disposed || _context is null)
            return Remember(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));

        IDownloadStore store = await StoreAsync(_context, ct);

        return Remember(await store.AllowAgainAsync(handle, ct)
            ? SaveSettingsOutcome.Done("Allowed again. It can be picked on the next search.")
            : SaveSettingsOutcome.Failure("That release is not being skipped any more."));
    }

    /// <summary>
    /// A button on the downloads page, applied to the running engine.
    ///
    /// <para>
    /// These go through the pipeline rather than the store because pausing something means
    /// pausing it, not writing down that it is paused. The pipeline is the one the cadences
    /// use, so the engine acted on here is the engine actually holding the torrent.
    /// </para>
    /// </summary>
    private async Task<SaveSettingsOutcome> OnDownloadAsync(
        Func<DownloadOrchestrator, Task<bool>> act,
        string done,
        CancellationToken ct,
        string nothingHappened = "That download is no longer one this plugin is holding.")
    {
        if (_disposed || _context is null)
            return Remember(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));

        try
        {
            DownloadPipeline pipeline = await PipelineAsync(_context, ct);

            return Remember(await act(pipeline.Orchestrator)
                ? SaveSettingsOutcome.Done(done)
                : SaveSettingsOutcome.Failure(nothingHappened));
        }
        catch (Exception failure)
        {
            // Reported rather than thrown: this is a button, and a stack trace in the
            // dashboard tells its reader nothing they can act on.
            _context.Logger.LogWarning(failure, "Torrent Downloader could not act on a download.");

            return Remember(SaveSettingsOutcome.Failure("That did not work. The server log says why."));
        }
    }

    public Task<SaveSettingsOutcome> AddSourceAsync(SaveSettingsRequest request, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleAddSourceAsync(request, ct));

    public Task<SaveSettingsOutcome> AddIndexerAsync(CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleAddIndexerAsync(ct));

    public Task<SaveSettingsOutcome> RemoveIndexerAsync(int index, CancellationToken ct = default) =>
        SaveAsync(handler => handler.HandleRemoveIndexerAsync(index, ct));

    private async Task<SaveSettingsOutcome> SaveAsync(Func<SettingsSaveHandler, Task<SaveSettingsOutcome>> handle)
    {
        if (_disposed)
        {
            return Remember(SaveSettingsOutcome.Failure("Torrent Downloader is unavailable."));
        }

        return Remember(await handle(new SettingsSaveHandler(SettingsGateway, _clock)));
    }

    /// <summary>
    /// What the last action said, kept until the next view is built.
    ///
    /// <para>
    /// The client throws away an action's response body and re-fetches the view, so this is
    /// the only way a refusal reaches the person who caused it. In memory rather than in the
    /// configuration: it is true for one render, and writing it to disk would make a
    /// transient sentence outlive the server.
    /// </para>
    /// </summary>
    private ActionNotice? _notice;

    private SaveSettingsOutcome Remember(SaveSettingsOutcome outcome)
    {
        _notice = new ActionNotice(
            outcome.Succeeded ? outcome.Message ?? "Saved." : outcome.Error ?? "That did not work.",
            !outcome.Succeeded);

        return outcome;
    }

    /// <summary>Reads the pending notice and clears it, so one press is reported once.</summary>
    private ActionNotice? TakeNotice() => Interlocked.Exchange(ref _notice, null);

    private sealed record ActionNotice(string Message, bool Failed);

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

        AnnounceOnce(context, jobName);

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

    private int _announced;

    /// <summary>
    /// Says the plugin is alive, once, on whichever cadence fires first.
    ///
    /// <para>
    /// Every other line this plugin writes reports something happening. That leaves an idle
    /// plugin and a dead one looking exactly alike in the log, and they are not the same
    /// problem at all - one waits, the other needs fixing. Chasing that difference on a
    /// real server cost the better part of an hour, watching a quarter-hourly cadence that
    /// might simply not have come round yet.
    /// </para>
    ///
    /// <para>
    /// The transfers cadence runs every minute, so this appears within a minute of the
    /// plugin loading rather than within fifteen.
    /// </para>
    /// </summary>
    private void AnnounceOnce(IPluginContext context, string jobName)
    {
        if (Interlocked.Exchange(ref _announced, 1) != 0)
            return;

        context.Logger.LogInformation(
            "Torrent Downloader is awake: version {Version}, first cadence to fire was {Job}.",
            Version,
            jobName);
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

        TransfersCycle cycle = await pipeline.Orchestrator.TransfersCycleAsync(ct);

        if (cycle.Imported > 0)
            context.Logger.LogInformation("Torrent Downloader handed {Count} finished download(s) to the intake.", cycle.Imported);

        if (cycle.PutBack == 0)
            return;

        // At once, not at the next cadence. A download that failed leaves its episodes
        // exactly as missing as they were before anything was grabbed, and the answer to a
        // missing episode is the same answer as always: look for another release. The
        // failed one is blacklisted, so this cannot pick it again. Waiting six hours to do
        // something the plugin already knows needs doing is the difference between a queue
        // that drains and one that looks stuck.
        context.Logger.LogInformation(
            "Torrent Downloader put {Count} episode(s) back after a failed download; looking again now.",
            cycle.PutBack);

        await RunSearchAsync(context, ct);
    }

    // A show with at least one episode on the server, still going out, is one this plugin
    // works on - not the library's full catalogue, which on a real server was 1973 episodes
    // of things nobody had ever put a file of on disk.
    private async Task RunFeedAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        WantedRefresh refresh = await pipeline.Orchestrator.RefreshWantedAsync(ct);

        context.Logger.LogInformation(
            "Torrent Downloader is missing {Count} episode(s) across {Shows} show(s) it follows.",
            refresh.Wanted,
            refresh.Shows);

        // Said out loud, because a plugin that quietly decides to want nothing is one the
        // owner concludes is broken. These are the lines that answer "why is it idle", and
        // they are separate because the two answers are: nobody has it, and nobody is
        // making any more of it.
        if (refresh.NotOnTheServer > 0)
        {
            context.Logger.LogInformation(
                "Torrent Downloader is leaving {Count} show(s) alone: the library lists them but no episode of them is on the server.",
                refresh.NotOnTheServer);
        }

        if (refresh.Finished > 0)
        {
            context.Logger.LogInformation(
                "Torrent Downloader is leaving {Count} show(s) alone: they have ended or been cancelled, so nothing more of them is coming.",
                refresh.Finished);
        }

        // Then whatever the feeds have posted since the last quarter of an hour. This runs
        // after the refresh on purpose: an episode that aired an hour ago is only wanted
        // once the refresh has noticed it is missing, and doing both in one tick means it
        // is grabbed in that same tick rather than fifteen minutes later.
        FeedCycle feed = await pipeline.Orchestrator.FeedCycleAsync(ct);

        // Both numbers, because "matched 40, grabbed 0" and "matched 0" are different
        // problems and one line has to tell them apart.
        if (feed.Matched > 0)
        {
            context.Logger.LogInformation(
                "Torrent Downloader found feed releases for {Matched} wanted episode(s) and grabbed {Grabbed}.",
                feed.Matched,
                feed.Grabbed);
        }
    }

    // A cycle now asks about every episode that could be searched rather than ten of them,
    // so on a library that is behind it can outlast the cadence that started it. Two
    // overlapping cycles read the same wanted list and would grab the same episode twice,
    // from two indexers, into two files. Skipped rather than queued: the next tick is five
    // minutes away and the work is the same work.
    private readonly SemaphoreSlim _searchLock = new(1, 1);

    private async Task RunSearchAsync(IPluginContext context, CancellationToken ct)
    {
        if (!await _searchLock.WaitAsync(0, ct))
        {
            context.Logger.LogInformation("Torrent Downloader is still working through the last search; skipping this one.");
            return;
        }

        try
        {
            await SearchOnceAsync(context, ct);
        }
        finally
        {
            _searchLock.Release();
        }
    }

    private async Task SearchOnceAsync(IPluginContext context, CancellationToken ct)
    {
        DownloadPipeline pipeline = await PipelineAsync(context, ct);

        SearchCycle cycle = await pipeline.Orchestrator.SearchCycleAsync(ct);

        if (cycle.Grabbed > 0)
        {
            context.Logger.LogInformation("Torrent Downloader started {Count} download(s).", cycle.Grabbed);
            return;
        }

        // Said out loud, every cycle that asked. This used to log only when something was
        // grabbed, so a cadence that had quietly stopped searching altogether looked exactly
        // like one working through a backlog nobody is seeding - and it stayed that way for
        // a day. One line per five minutes is worth never being in that position again.
        if (cycle.Searched > 0)
        {
            context.Logger.LogInformation(
                "Torrent Downloader searched for {Count} episode(s) and found nothing worth taking.",
                cycle.Searched);
        }
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
    // The plugin sits beside films and shows rather than only in the admin panel: the
    // question it answers - where is episode five - is one asked while looking at the
    // library, not while configuring a server. One entry, landing on the overview, with the
    // other pages behind the tab bar; a second entry in the dashboard's settings section
    // goes straight to the settings page, because that is where an owner looks for a
    // plugin's configuration.
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
                // The library as a whole, not the video medium. What this plugin does is
                // keep a library complete; it is not a thing you reach while looking at one
                // film. The section is also what decides the URL - the host builds the
                // prefix from it - so this is what puts the pages under /libraries rather
                // than off in a namespace of their own.
                Section = PluginUiSection.Library,
                Label = PluginIdentity.Name,
                Icon = "download",
                Route = "/",
            },
        ];

    /// <summary>
    /// Every page this plugin serves.
    ///
    /// <para>
    /// Not decoration. The server reads this and serves it as the plugin's pages; the client
    /// registers a named route for each. A page nobody declares falls back to a wildcard
    /// that covers the legacy <c>/plugins/{id}/…</c> mount and not the
    /// <c>/video/plugins/{id}/…</c> one this plugin sits behind - so every tab beyond the
    /// two original routes would reach the app's 404 without this.
    /// </para>
    /// </summary>
    public PluginRouteTable Routes => Pages.Routes;

    /// <summary>
    /// What the lifecycle owns about a page render, and nothing about the pages themselves.
    ///
    /// <para>
    /// Two things belong here and not in <see cref="PluginPages"/>. A request after Dispose
    /// is not the caller's bug the way a tick is - the host may still be draining an
    /// in-flight render while tearing the plugin down - so it answers with something
    /// renderable rather than throwing into the request pipeline. And the notice: what the
    /// last button press did is this class's to remember, because this class is what the
    /// button called.
    /// </para>
    /// </summary>
    public async Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        if (_disposed)
            return DisposedView();

        PluginView view = await new PluginPages(Context, SettingsGateway, StoreForPagesAsync, _clock).BuildAsync(request, ct);

        return TakeNotice() is { } notice ? Pages.WithNotice(view, notice.Message, notice.Failed) : view;
    }

    private Task<IDownloadStore> StoreForPagesAsync(CancellationToken ct) => StoreAsync(Context, ct);

    // No I/O happens before this, so there is nothing to cancel - the disposed case never
    // reaches a page.
    private static PluginView DisposedView() =>
        PluginViews.Declarative(
            Ui.EmptyState(
                "settings-unavailable",
                "Torrent Downloader is unavailable",
                "This plugin is disabled or is being unloaded."));

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
