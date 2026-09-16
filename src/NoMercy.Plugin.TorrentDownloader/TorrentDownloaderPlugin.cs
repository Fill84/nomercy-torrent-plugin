using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Events.Library;
using NoMercy.Events.Plugins;
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
/// The plugin the server loads: its identity, its one cycle and its pages.
/// </summary>
public sealed class TorrentDownloaderPlugin : IPlugin, IScheduledTaskPlugin, IUiPlugin, IPluginServiceRegistrator
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ActivityJournal _journal = new();
    private readonly SemaphoreSlim _migrating = new(1, 1);
    private Store? _database;
    private EpisodeRepository? _episodes;
    private GrabRepository? _grabs;
    private bool _migrated;
    private IPluginContext? _context;
    private SettingsStore? _settings;
    private LiveSnapshot? _live;
    private Chain? _chain;
    private BittorrentEngine? _engine;

    /// <summary>
    /// Whether the advanced blocks are drawn on the settings page.
    /// </summary>
    /// <remarks>
    /// <strong>A display state, and it is written nowhere.</strong> The design
    /// of 12 September is explicit: Show advanced changes what is drawn and
    /// nothing about what the plugin does, and a field hidden behind it still
    /// applies. So it is not in the settings, not in config.json, and it does
    /// not survive a restart - which costs an owner one click and keeps a
    /// switch that decides nothing out of the file that decides everything.
    /// </remarks>
    public bool ShowAdvanced { get; private set; }

    /// <summary>Flips it, and answers what it became.</summary>
    public bool ToggleAdvanced()
    {
        ShowAdvanced = !ShowAdvanced;

        return ShowAdvanced;
    }

    /// <summary>Tells the open pages that a transfer has moved.</summary>
    /// <remarks>
    /// <para>
    /// <see cref="Moved"/> was called from two places — a journal entry and a
    /// cycle starting or stopping — and a download in flight is neither. So the
    /// Downloads page drew the figures of the moment it was opened and then sat
    /// there: the owner watched 41.2% of 36.1 GB while ten megabytes a second
    /// were arriving, and only a refresh by hand moved it.
    /// </para>
    /// <para>
    /// Only while something is really transferring, so a plugin with nothing to
    /// say still says nothing — and <see cref="LiveSnapshot.MinimumInterval"/>
    /// is the floor either way, so this cannot push oftener than a page can be
    /// drawn.
    /// </para>
    /// </remarks>
    private Heartbeat? _heartbeat;
    private HttpClient? _trackerHttp;
    private Transfers? _transfers;

    /// <summary>What the server has said about the encodes this plugin asked for.</summary>
    /// <remarks>
    /// Built once and kept, because it is only worth anything for having been
    /// listening: one built per transfers pass would know nothing about an
    /// encode that finished before it existed.
    /// </remarks>
    private EncoderSays? _says;
    private int _settled;
    private SourceLedgerRepository? _ledger;
    private IReadOnlyList<SourceDefinition>? _shipped;

    /// <summary>What the last search cycle decided, for the pages that say so.</summary>
    private CycleReport? _lastCycle;

    /// <summary>How the last run ended, read from the store once and kept up to date after.</summary>
    private LastRun? _lastRun;
    private bool _lastRunRead;

    /// <summary>When the running search began, or null when none runs.</summary>
    private DateTimeOffset? _runStartedAt;

    private RunRepository? _runs;

    private CadenceRepository? _cadences;

    /// <summary>
    /// Decides which cadence is due, because the host reads <see cref="Jobs"/>
    /// only when the plugin is installed, hot-swapped or enabled and a saved
    /// cadence has to take effect without waiting for one of those. S12-05.
    /// </summary>
    private Clock? _clock;

    /// <summary>
    /// When the next cycle is due, read from <see cref="_clock"/> whenever the
    /// clock is wound and cached here purely so <see cref="CurrentCycle"/> can
    /// stay synchronous — it is a status-bar figure, not a scheduling decision.
    /// </summary>
    private DateTimeOffset? _nextCycleDue;

    /// <summary>The running cycle's own stopping token, or null when none runs.</summary>
    private CancellationTokenSource? _cycle;

    /// <summary>The guard that keeps two cycles from running together.</summary>
    private readonly OneAtATime _running = new();


    /// <summary>Keeps two transfers passes from staging the same file twice.</summary>
    private readonly OneAtATime _transfersRunning = new();

    /// <summary>A pass was asked for while one ran, so that one goes round again.</summary>
    private int _transfersAgain;

    /// <summary>Whether a start has put back what the client held.</summary>
    private int _startedUp;

    /// <summary>What the plugin hears the server say it has loaded through.</summary>
    private IDisposable? _loaded;

    /// <summary>What the cycle's own state is read and written under.</summary>
    private readonly Lock _cycleLock = new();

    /// <summary>A cycle has been started and its maintenance has not run yet.</summary>
    /// <remarks>
    /// What the status bar draws and what the Run button is disabled by — the
    /// owner's ruling of 13 September 2026: Running means the whole cycle, not
    /// the searching half of it, because downloads and encodes a cycle started
    /// are still that cycle.
    /// </remarks>
    private bool _open;

    /// <summary>Feed and search are in flight.</summary>
    private bool _finding;

    /// <summary>A trigger arrived while they were, so they run once more.</summary>
    private bool _again;

    /// <summary>The cycle that is open, so a caller can wait for it.</summary>
    private Task? _cycling;

    /// <summary>Whether anybody has a page of this plugin open.</summary>
    /// <remarks>
    /// Built with the plugin rather than with the client, because a page can be
    /// opened long before anything is downloaded, and the client is built on
    /// first use.
    /// </remarks>
    private readonly Onlookers _onlookers = new();

    /// <summary>What the plugin listens to the server's library scans through.</summary>
    private IDisposable? _scanning;

    /// <summary>When the next cycle falls due, set to that moment and no sooner.</summary>
    /// <remarks>
    /// One shot, never a repeat: it is wound for the moment the owner's cadence
    /// next comes round and wound again when a cycle closes. Nothing here ever
    /// wakes to ask whether something is due.
    /// </remarks>
    private ITimer? _due;

    private int _unconfigured;
    private int _overlapping;
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
    /// What the host ticks this plugin on, which is not the owner's cadence.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hourly, and it starts nothing at all — see <see cref="ExecuteAsync(string, CancellationToken)"/>.
    /// The host reads a plugin's schedule when the plugin loads and never asks
    /// again, so a cadence the owner changes could never reach it: the owner
    /// changed one on 3 September 2026, watched the old one go on firing, and
    /// reasonably concluded the setting did nothing. The plugin keeps its own
    /// clock instead, and this tick only makes sure that clock is wound.
    /// </para>
    /// <para>
    /// Declared rather than declined because the contract has no way to
    /// decline: a plugin whose <see cref="Jobs"/> is empty is registered under
    /// this single expression instead, so there is no "no schedule" to ask for.
    /// </para>
    /// </remarks>
    public string CronExpression => JobNames.HostCron;

    /// <summary>
    /// One job, and it is the same tick as <see cref="CronExpression"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This used to be four, and then one every minute.</strong>
    /// Transfers, feed, search and maintenance are not four jobs on four
    /// schedules — they are the steps of one cycle, each started by the last
    /// one finishing. Transfers was <c>* * * * *</c> and not because anything
    /// about transfers wanted a minute: the torrent client did its whole
    /// housekeeping inside the method the pages call to draw a table, so
    /// without a tick a minute it stopped expiring magnets, noticing stalls,
    /// noticing completions and writing its resume files. `S12-13` gave those
    /// their own moments, which is what let this go.
    /// </para>
    /// </remarks>
    public IReadOnlyList<PluginScheduledJob> Jobs { get; } = [new(JobNames.Cycle, JobNames.HostCron)];

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
        _settings = new(
            context.Configuration,
            context.Secrets,
            volumeOf: null,

            // Where the server says it can write, used only to make a refused
            // folder something the owner can act on. media-server #32.
            storage: () => context.Services.GetService(typeof(IPluginStorage)) as IPluginStorage);
        _database = new(context.DataFolderPath);
        _episodes = new(_database);
        _grabs = new(_database);
        _ledger = new(_database);
        _runs = new(_database);
        _cadences = new(_database);
        _clock = new(_cadences, TimeProvider.System);
        _live = new(context.Hub, _journal, context.Logger, CurrentCycle, told: _onlookers.Told);

        // The page heartbeat beats while somebody is looking and at no other
        // time. It was a timer set to go off every second for the life of the
        // server, pushing every change to pages nobody had open — and every push
        // makes the web app fetch the whole view again. The owner saw that as
        // the web app flooded with pushes that told it nothing.
        _onlookers.Arrived += () => _heartbeat?.StartBeating();
        _onlookers.Left += () => _heartbeat?.StopBeating();

        // The one line that makes the pages live. Without it LiveSnapshot is a
        // push nothing ever asks for: a dashboard opened during a cycle showed
        // the stage the plugin was on when the page loaded and never moved
        // again, and the owner reported it before any test did.
        _journal.Recorded += Moved;

        // The server finishing a library scan, which is one of the three things
        // that start a cycle. The owner's own words on 13 September 2026: the
        // same chain that Run starts should also be started "door de library
        // update van de media-server zelf".
        //
        // The scan and not a file appearing. LibraryFileWatcher raises
        // FileCreatedEvent live, and an encode this plugin asked for lands a
        // file in the library — so a cycle hung on that would start itself, for
        // ever.
        _scanning = context.EventBus.Subscribe<LibraryScanCompletedEvent>((scan, _) =>
        {
            Trigger($"the server finished scanning {scan.LibraryName}");

            // And a transfers pass, because a scan is the server saying the
            // library has just been read — which is what closes a grab waiting
            // on an encode the server never says anything about: one the owner
            // took out of the queue by hand, or one that ended with nothing to
            // encode. The owner's ruling of 14 September 2026 is that the
            // library decides, not a clock.
            StartTransfers();

            return Task.CompletedTask;
        });

        // And what a start owes — the torrents that were running, the clock —
        // when the server says this plugin has loaded, which is after this
        // method. Not here: this runs while the server is still coming up, and
        // all of it is I/O.
        _loaded = context.EventBus.Subscribe<PluginLoadedEvent>((loaded, publishing) =>
        {
            if (string.Equals(loaded.PluginId, PluginIdentity.IdText, StringComparison.OrdinalIgnoreCase))
            {
                _ = Task.Run(() => StartUpAsync(Lifetime), CancellationToken.None);
            }

            return Task.CompletedTask;
        });

        // And the server's own encoding events, listened to from the moment
        // this plugin is loaded rather than from its first transfers pass.
        // Built here because a listener is only worth anything for having been
        // listening: one made later knows nothing about an encode that
        // finished before it existed, and a restart during an encode is
        // exactly when that matters. Subscribing is not I/O, so it breaks
        // nothing this method promises.
        _ = Says();
    }

    /// <summary>The database, migrated up to date before anything opens it.</summary>
    private async Task<Store> DatabaseAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _database ?? throw new InvalidOperationException("The plugin has not been initialised.");
    }

    /// <summary>
    /// The episode store, migrated up to date before it is first handed out.
    /// </summary>
    /// <remarks>
    /// Migrating on first use rather than during <c>Initialize</c>, which does
    /// no I/O. Behind a semaphore because a cycle and a page render can
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
        return ExecuteAsync(JobNames.Cycle, ct);
    }

    public async Task ExecuteAsync(string jobName, CancellationToken ct = default)
    {
        if (!JobNames.Answers(jobName))
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
        // plugin and a host tick that returns must not take it down with it.
        using CancellationTokenSource work = CancellationTokenSource.CreateLinkedTokenSource(Lifetime);

        // What a start owes, for a server that never said this plugin loaded.
        // Once: every part of it remembers that it has run.
        await StartUpAsync(work.Token);

        // One tick, and it starts nothing new. The owner's cadence is kept by this
        // plugin's own clock, which is set to the moment the next cycle is due
        // rather than woken to ask whether one is; the host cannot be told
        // about it, because a schedule is read from a plugin when the plugin
        // loads and never asked for again. All this does is make sure that
        // clock is wound, which matters after a restart and at no other time.
        await WindAsync(work.Token);
    }

    /// <summary>
    /// Starts a cycle, or adds to the one that is already open.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The one door in.</strong> Three things start a cycle — the owner
    /// pressing Run, the server finishing a library scan, and the owner's own
    /// cadence coming round — and all three arrive here.
    /// </para>
    /// <para>
    /// <strong>A trigger during an open cycle is an addition, not a second
    /// cycle and not a dropped one.</strong> The owner's words on 13 September
    /// 2026: "een nieuwe run is een toevoeging van de draaiende run". What the
    /// open cycle has already taken is written down as it takes it, so the feed
    /// and search it runs again exclude those by themselves and nothing is
    /// grabbed twice.
    /// </para>
    /// </remarks>
    /// <returns>Whether this opened a cycle, as opposed to joining one.</returns>
    private bool Trigger(string why)
    {
        lock (_cycleLock)
        {
            if (_open)
            {
                _again = true;

                _context?.Logger.LogInformation(
                    "{Why}, and a cycle is already open, so it was added to that one.", why);

                return false;
            }

            // Set on the caller's thread and before anything is started, so a
            // page drawn the instant the Run button is pressed already says the
            // cycle is running. It used to be claimed inside the task, and the
            // owner saw a Run button still enabled on a run they had started.
            _open = true;
            _finding = true;

            _context?.Logger.LogInformation("{Why}, so a cycle was started.", why);

            // Kept, so a caller that wants the work done before it looks at the
            // result can wait for it. The button never does - an HTTP request
            // holding a cycle open threw away half an hour when a tab closed,
            // which is F1 - but RunCycleAsync does, and so does every test of
            // the chain.
            _cycling = Task.Run(() => CycleThroughAsync(Lifetime), CancellationToken.None);
        }

        return true;
    }

    /// <summary>
    /// One whole cycle, waited for: feed, search, and maintenance once there is
    /// nothing left in hand.
    /// </summary>
    /// <remarks>
    /// What <see cref="StartRun"/> starts, with the waiting. Where a cycle is
    /// already open this waits for that one rather than starting a second, which
    /// is the same rule <see cref="Trigger"/> follows.
    /// </remarks>
    public async Task RunCycleAsync(CancellationToken ct = default)
    {
        if (ct.IsCancellationRequested)
        {
            // Asked for by something that has already gone, which is what the
            // plugin's own lifetime looks like once the server has said it is
            // shutting down. A shutdown is not a fault, and a cycle started for
            // nobody is work thrown away.
            return;
        }

        Trigger("a caller asked for a cycle and is waiting for it");

        Task? going;

        lock (_cycleLock)
        {
            going = _cycling;
        }

        if (going is null)
        {
            return;
        }

        try
        {
            await going.WaitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // The caller stopped waiting. The cycle runs on the plugin's own
            // lifetime and carries on, which is F1: a browser tab closed after
            // half an hour must not throw away twenty-nine minutes of work.
        }
    }

    /// <summary>
    /// A search, then again for every trigger that arrived meanwhile.
    /// </summary>
    /// <remarks>
    /// The searching half of a cycle. What it finds it hands to the torrent
    /// client, and the rest of the cycle is the client, the stager and the
    /// encoder telling each other what they have done — see
    /// <see cref="SettledAsync"/> for where it ends.
    /// </remarks>
    private async Task CycleThroughAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                // No harvest ahead of it any more: the search cycle reads every
                // name source's feed itself, at its start (docs/specs/run.md).
                if (_running.TryEnter())
                {
                    try
                    {
                        await CycleAsync(ct, only: null, holding: true);
                    }
                    finally
                    {
                        _running.Leave();
                    }
                }

                lock (_cycleLock)
                {
                    if (!_again)
                    {
                        break;
                    }

                    _again = false;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // The plugin is shutting down, or the owner pressed Stop.
        }
        catch (Exception wrong)
        {
            _context?.Logger.LogError(wrong, "The cycle stopped: {Reason}", wrong.Message);
        }
        finally
        {
            lock (_cycleLock)
            {
                _finding = false;
            }
        }

        await SettledAsync(ct);
    }

    /// <summary>
    /// Closes the cycle and runs maintenance, once there is nothing left in
    /// hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Called where work might have ended, never on a clock.</strong>
    /// That is after the searching half of a cycle, and after every transfers
    /// pass — and a transfers pass runs when a download finishes or the server
    /// says something about an encode, so this is asked exactly when the answer
    /// can have changed.
    /// </para>
    /// <para>
    /// <strong>In hand</strong> is what the client is really holding and what is
    /// waiting on an encode — the owner's ruling of 13 September 2026. A grab
    /// written down but not yet started does not hold a cycle open, because one
    /// that stuck would hold it open for ever.
    /// </para>
    /// <para>
    /// Maintenance last, and this is why it waits: it sweeps download folders
    /// no grab answers for, and a sweep that runs while a download is in flight
    /// is a sweep that can take it.
    /// </para>
    /// </remarks>
    private async Task SettledAsync(CancellationToken ct)
    {
        lock (_cycleLock)
        {
            if (!_open || _finding)
            {
                return;
            }
        }

        if (await InHandAsync(ct))
        {
            return;
        }

        lock (_cycleLock)
        {
            // Read again now the slow part is over: a trigger or a transfers
            // pass may have arrived while the store was being asked, and this
            // is the only place a cycle is closed.
            if (!_open || _finding)
            {
                return;
            }

            _open = false;
        }

        try
        {
            await MaintainAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        catch (Exception wrong)
        {
            _context?.Logger.LogWarning(wrong, "Maintenance failed: {Reason}", wrong.Message);
        }

        // Written down however it ended. The clock's record is of when a cycle
        // last ended, not of when one last succeeded: a failure treated as
        // though it never finished would have the next one come round at once
        // instead of waiting out the owner's own interval.
        try
        {
            await (await ClockAsync(ct)).FinishedAsync(JobNames.Cycle, ct);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            _context?.Logger.LogWarning(wrong, "The cycle's finish was not written down: {Reason}", wrong.Message);
        }

        // The cycle is over, and the pages say so.
        Moved();

        // And the clock is set for the next one.
        await WindAsync(ct);
    }

    /// <summary>Whether the plugin is still holding work a cycle started.</summary>
    private async Task<bool> InHandAsync(CancellationToken ct)
    {
        try
        {
            // The client first, because it answers without touching a disk.
            if (_engine is BittorrentEngine client && client.Watching)
            {
                return true;
            }

            if (await ConfiguredAsync(ct) is null)
            {
                return false;
            }

            return (await (await GrabsAsync(ct)).OpenAsync(ct))
                .Any(one => one.State is GrabState.Staged or GrabState.Dispatched);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            // Being wrong here costs a cycle that stays open until the next
            // thing happens; being wrong the other way runs the folder sweep
            // while a download is in flight.
            _context?.Logger.LogWarning(wrong, "What is still in hand could not be read: {Reason}", wrong.Message);

            return true;
        }
    }

    /// <summary>
    /// Sets the clock for the moment the next cycle falls due.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>Wound, not ticking.</strong> One shot at the moment the owner's
    /// cadence next comes round, and wound again when a cycle closes. Nothing
    /// wakes to ask whether a cycle is due — it wakes because one is.
    /// </para>
    /// <para>
    /// The host's own tick calls this too, which is all that tick does. A
    /// schedule is read from a plugin when the plugin loads and never asked for
    /// again, so the host cannot be told the owner's cadence; what it can do is
    /// make sure the plugin's own clock is wound, which matters after a restart
    /// and at no other time.
    /// </para>
    /// </remarks>
    private async Task WindAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return;
        }

        DateTimeOffset? next = await (await ClockAsync(ct)).NextAsync(
            JobNames.Cycle, settings.Cadences.Cycle, ct);

        _nextCycleDue = next;

        lock (_cycleLock)
        {
            _due?.Dispose();
            _due = null;

            if (next is not DateTimeOffset moment)
            {
                // No cadence this plugin can read. Run and a library scan still
                // start one; nothing is guessed.
                return;
            }

            TimeSpan wait = moment - DateTimeOffset.UtcNow;

            _due = TimeProvider.System.CreateTimer(
                _ => Trigger("the owner's cadence came round"),
                null,
                wait > TimeSpan.Zero ? wait : TimeSpan.Zero,
                Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Wound without a caller to fault, for the timer that wound itself.</summary>
    private async Task WindGuardedAsync(CancellationToken ct)
    {
        try
        {
            await WindAsync(ct);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            _context?.Logger.LogWarning(
                wrong, "When the next cycle is due could not be worked out: {Reason}", wrong.Message);
        }
    }

    /// <summary>The clock, once the database behind it has been migrated.</summary>
    private async Task<Clock> ClockAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _clock ?? throw new InvalidOperationException("The plugin has not been initialised, so it has no clock yet.");
    }

    /// <summary>
    /// One pass over everything the torrent client is holding.
    /// </summary>
    /// <remarks>
    /// Run when a download finishes, when the server says something about an
    /// encode and when it says a library scan finished, because a completion
    /// nobody notices is an episode nobody gets.
    /// It runs on a plugin that has folders and does nothing at
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

        HostLibrary library = new(Context.Library);

        _transfers ??= new(
            engine,
            await GrabsAsync(ct),
            library,
            await AppliedAsync(ct),
            new Stager(_journal, Context.Logger),

            // The contract where this server offers it, and the older way where
            // it does not. media-server #30 and #35 are what made the first of
            // those possible.
            EncodeGateway.For(Context.Services, _journal, Context.Logger),
            _journal,
            Context.Logger,
            time: null,

            // Where the server says what became of an encode. Without it a
            // failed job and a slow one look the same, and a failed one is
            // waited on until the owner cancels it.
            says: Says(),

            // What adds a show the owner does not have yet. Until this, a pack
            // for one could not be dispatched at all: an encode is asked for by
            // an episode id and a show with no row has none.
            imports: Imports());

        await _transfers.TickAsync(
            settings.IncompleteFolder,
            settings.IntakeFolder,
            ct,

            // For anything this tick has to add again: the magnet the store kept
            // names no tracker, so without these it comes back with none.
            settings.Client.DefaultTrackers);
    }

    /// <summary>
    /// What the server has said about this plugin's encodes, listening from the
    /// first time it is wanted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>And its saying so is what sets the rest going.</strong> Staging
    /// the next episode, deleting a download the encoder has finished with and
    /// marking a grab done all used to wait for the transfers cadence to come
    /// round, which is why that cadence was a minute. They happen when the
    /// encode really ends now.
    /// </para>
    /// <para>
    /// The pass is started and not awaited, on the plugin's own lifetime: this
    /// is called from the media server's event bus, which publishes to every
    /// subscriber in turn, and a plugin that staged a file and asked for an
    /// encode before returning would hold up everything else the server wanted
    /// to tell about its own encode.
    /// </para>
    /// </remarks>
    private EncoderSays Says()
    {
        if (_says is not null)
        {
            return _says;
        }

        _says = new(Context.EventBus, Context.Logger);

        _says.Said += media =>
        {
            _context?.Logger.LogDebug("The server has spoken about encode {Media}.", media);

            StartTransfers();

            // And the pages, which draw what the History says about a dispatch.
            Moved();
        };

        return _says;
    }

    /// <summary>
    /// One transfers pass, with nothing left to escape it and never two at once.
    /// </summary>
    /// <remarks>
    /// Started from an event rather than from a caller, so there is nobody to
    /// throw to: an exception escaping would be an unobserved task exception,
    /// which is a fault with no line in this plugin's own log at all. Dropped
    /// rather than queued when one is already running — the pass reads
    /// everything fresh, so the one in flight covers whatever this one would
    /// have.
    /// </remarks>
    private async Task TransfersGuardedAsync(CancellationToken ct)
    {
        if (!_transfersRunning.TryEnter())
        {
            // Not dropped. The pass that is running read the client before this
            // was asked for, so a download that finished a moment ago is one it
            // may not have seen — dropped here, it would sit in the incomplete
            // folder until something else happened to start another pass. The
            // one running goes round once more instead.
            Volatile.Write(ref _transfersAgain, 1);

            return;
        }

        try
        {
            do
            {
                Volatile.Write(ref _transfersAgain, 0);

                await TransfersAsync(ct);
            }
            while (Volatile.Read(ref _transfersAgain) != 0 && !ct.IsCancellationRequested);
        }
        catch (OperationCanceledException)
        {
            // The plugin is shutting down.
        }
        catch (Exception wrong)
        {
            _context?.Logger.LogWarning(wrong, "A transfers pass failed: {Reason}", wrong.Message);
        }
        finally
        {
            _transfersRunning.Leave();
        }

        // Asked for while the guard was being let go: nobody is left to go round
        // for it, so it is taken on here rather than lost in the gap.
        if (Volatile.Read(ref _transfersAgain) != 0 && !ct.IsCancellationRequested)
        {
            StartTransfers();
        }

        // A pass is what turns a finished download into a staged file and a
        // staged file into an episode, so it is exactly where the last of the
        // work a cycle started can have gone. Asked here and after the search,
        // and on no clock at all.
        try
        {
            await SettledAsync(ct);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            _context?.Logger.LogWarning(wrong, "Closing the cycle failed: {Reason}", wrong.Message);
        }
    }

    /// <summary>Starts a transfers pass on the plugin's own lifetime, and answers at once.</summary>
    /// <remarks>
    /// The one door into a pass for everything that is not a caller waiting on
    /// it: a download finishing, the server saying something about an encode, a
    /// start. Each of those is an event with nobody to hand a task back to.
    /// </remarks>
    private void StartTransfers()
    {
        _ = Task.Run(() => TransfersGuardedAsync(Lifetime), CancellationToken.None);
    }

    /// <summary>
    /// What a start owes, once: the housekeeping, the torrents that were in the
    /// client when it stopped, and the clock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what nothing did once the transfers job was gone.</strong>
    /// The torrent client is built on first use, and the job ticking every minute
    /// was what used it first — so it was also what put back every download that
    /// was running when the server stopped. With it gone, those sat untouched
    /// until somebody opened a page.
    /// </para>
    /// <para>
    /// Started when the server says this plugin has loaded, which is after
    /// <c>Initialize</c> — that must not do I/O, because the server is still
    /// coming up while it runs. The host's own tick asks too, for a server that
    /// never says it; either way it runs once.
    /// </para>
    /// </remarks>
    private async Task StartUpAsync(CancellationToken ct)
    {
        try
        {
            await SettleOnceAsync(ct);

            // Once, and only once there is somewhere to put a download: a pass
            // on a plugin with no folders has nothing to re-add and nowhere to
            // stage to, and must still happen when the owner does set them.
            if (await ConfiguredAsync(ct) is not null && Interlocked.Exchange(ref _startedUp, 1) == 0)
            {
                // The pass builds the client, and recovery puts back every grab
                // that is not done. One already whole on disk says so as it
                // opens, which stages it — the download that finished while the
                // server was down.
                StartTransfers();
            }

            await WindAsync(ct);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            _context?.Logger.LogWarning(wrong, "Starting up failed: {Reason}", wrong.Message);
        }
    }

    /// <summary>
    /// Looks for every missing episode and takes the winner of what its show's settings accept.
    /// </summary>
    /// <remarks>
    /// The report is kept so the pages can say what this cycle decided about
    /// each episode. Nothing is written to the store here: recording a grab is
    /// <c>S6-01</c>, and a decision the plugin cannot yet act on is not a fact
    /// about an episode.
    /// </remarks>
    private async Task SearchAsync(CancellationToken ct, EpisodeKey? only = null)
    {
        // The library first, and before the chain: what the plugin should be
        // looking for is derived from the library every time and never
        // remembered as a fact. A cycle that read the store alone would decide
        // about whatever was true when somebody last refreshed it — and on a
        // fresh install, about nothing at all.
        //
        // Ahead of the chain because reading a library needs none of it, and a
        // plugin that cannot build a chain should still have pages that say
        // what is missing.
        await RefreshAsync(ct);

        if (await ChainAsync(ct) is not (Chain chain, Settings settings))
        {
            return;
        }

        IReadOnlyList<TrackedEpisode> tracked = await Tracked(ct);

        if (only is EpisodeKey wanted)
        {
            // One episode, and the whole chain for it. Narrowed here rather
            // than inside the cycle so that everything the cycle decides — what
            // a pack settles, what was refused — is decided the same way it is
            // on a full pass.
            tracked = [.. tracked.Where(one => one.Key == wanted)];
        }

        GrabRepository grabs = await GrabsAsync(ct);

        if (only is null)
        {
            // An episode something is already downloading is not a gap. It
            // stays missing until a file for it is in the library — which is
            // right — and without this the cycle read that as work and grabbed
            // the same release again: on 23 August 2026 three episodes of Sugar
            // had four identical grabs each, one per cycle.
            //
            // Never when the owner asked for one episode by hand. That is a
            // decision they have made about that episode, and refusing it
            // because a grab is open is refusing the thing they asked for.
            tracked = OpenGrabs.Excluding(
                tracked,
                [.. (await grabs.OpenAsync(ct)).SelectMany(one => one.Covers)]);
        }

        // Every challenge cleared first, in one pass, so the run itself needs no
        // browser at all. The owner's design of 11 September 2026: Chrome is
        // for getting past Cloudflare and for nothing else, and once the
        // cookies are in hand it can be shut.
        try
        {
            await chain.ClearTheWayAsync(settings, ct);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            // Never a reason to skip the search. Clearing up front is a saving,
            // not a requirement: a challenge that was not cleared here is one
            // the run meets when it reaches that host, which is what happened
            // before this pass existed at all. Letting it throw would lose the
            // whole cycle to a browser that would not start.
            Context.Logger.LogWarning(
                wrong,
                "The challenges could not be cleared before the run, so each host meets its own as it is asked.");
        }

        try
        {
            _lastCycle = await chain.Search(
                settings,

                // Each decision is written the moment it is made. Written at
                // the end instead, twenty-eight gaps meant half an hour in
                // which every page said nothing and a run stopped in the
                // meantime threw away everything it had decided.
                new CycleWriter(
                    tracked,
                    grabs,
                    await EpisodesAsync(ct),
                    () => DateTimeOffset.UtcNow)).RunAsync(
                tracked,
                new(
                    // What applies to each show, read once for the run: the
                    // names are judged against it (docs/specs/release-names.md).
                    await SettingsByShowAsync(tracked, ct),

                    // The hashes a download has already failed on. Without it
                    // the next cycle chooses the same release and fails the
                    // same way, for as long as the plugin runs.
                    await grabs.BlacklistedAsync(ct),

                    // Dry run is gone from the page and the stored settings —
                    // it was a testing switch on an owner's own page. The
                    // pipeline keeps the seam because its own tests decide a
                    // whole cycle with no torrent client behind it, but
                    // nothing here ever asks for one.
                    false,
                    settings.IncompleteFolder)
                {
                    DefaultTrackers = settings.Client.DefaultTrackers,

                    // Their tracker belongs to the torrents it issued and to
                    // nothing else.
                    OwnTrackerHosts = [.. settings.PrivateTrackers.Select(one => one.Host)],
                },
                ct);
        }
        finally
        {
            // The run is over, so the browser goes. Every tab it kept per site
            // is closed with it; the clearance those tabs earned is already in
            // the clearance store and the profile is a folder on disk, so
            // nothing is lost but the memory.
            //
            // Never on the run's own token. A run that was stopped arrives here
            // with that token already cancelled, and closing the browser on it
            // gave up before it began — so Stop, of all things, was the one
            // ending that could leave Chrome running.
            await chain.RunEndedAsync(CancellationToken.None);
        }

        // Everything the cycle decided is already written: CycleWriter put
        // each episode down as it was decided rather than all of them here.
        await KeepTrackersAsync(settings, _lastCycle.Trackers, ct);
    }

    /// <summary>
    /// One cycle, and never two at once.
    /// </summary>
    /// <remarks>
    /// The cadence and the Run button both come through here. Two cycles
    /// running together would ask every site twice and could grab the same
    /// release for one episode twice over, because what one has decided is
    /// state the other cannot see.
    /// </remarks>
    /// <param name="ct">The plugin's own lifetime, or the Stop button's, never a caller's request.</param>
    /// <param name="only">One episode to search for, or null for every one that is missing.</param>
    /// <param name="holding">
    /// Whether the caller already holds the guard. If it does, it lets it go;
    /// this lets go only what it took itself. It used to let go either way, and
    /// with <see cref="CycleThroughAsync"/> holding the guard around it that was
    /// a release in the gap between two — room for a search for one episode to
    /// take the guard and then have it let go under it.
    /// </param>
    private async Task CycleAsync(CancellationToken ct, EpisodeKey? only = null, bool holding = false)
    {
        if (!holding && !_running.TryEnter())
        {
            // Dropped, not queued: a tick that arrives during a cycle is one
            // the cycle is already doing the work of.
            SayOnce(ref _overlapping, "A cycle is already running, so this one was not started.");

            return;
        }

        using CancellationTokenSource cycle = CancellationTokenSource.CreateLinkedTokenSource(ct);

        _cycle = cycle;

        // Counted from nought, with nothing noted about any episode yet.
        _journal.RunStarted();

        // Already set when the button claimed the run, so the page said
        // "running since" from the instant it was pressed; a cycle nobody
        // pressed for sets it here.
        _runStartedAt ??= DateTimeOffset.UtcNow;
        DateTimeOffset started = _runStartedAt.Value;
        RunEnd how = RunEnd.Finished;

        try
        {
            await SearchAsync(cycle.Token, only);
        }
        catch (OperationCanceledException)
        {
            // Stopped on purpose — the owner pressed Stop, or the server is
            // going away. Not a fault, and the page says so, and when.
            how = RunEnd.Stopped;
        }
        catch (Exception wrong)
        {
            // Said, not swallowed. A run started from the button is a task
            // nobody awaits, so an exception here would go nowhere at all and
            // the page would show a cycle that began, ended, and explained
            // nothing.
            how = RunEnd.Failed;
            _journal.Failed(ActivityStage.Decide, "the search cycle", wrong.Message);
            _context?.Logger.LogError(wrong, "The search cycle stopped: {Reason}", wrong.Message);
        }
        finally
        {
            _cycle = null;

            // Nothing of the run stays on the page, however it ended. The owner
            // pressed Stop on 11 September 2026 and every row it had started
            // stayed where it was, which read as a pause.
            _journal.RunEnded();

            // Before the guard is let go, so a page drawn the moment the run is
            // over already says how it ended.
            _runStartedAt = null;
            await RememberAsync(new(started, DateTimeOffset.UtcNow, how));

            if (!holding)
            {
                _running.Leave();
            }

            // The last thing a run does. Nothing is recorded in the journal by
            // stopping, so without this the status bar keeps saying "Running"
            // on every page that was open when it finished.
            Moved();
        }
    }

    /// <summary>Whether anybody has a page of this plugin open, as far as can be known.</summary>
    /// <remarks>
    /// Known without asking: a page fetched says yes, a push nothing fetched
    /// after says no, and a long stretch with nothing to push lets it rest until
    /// the client does something again.
    /// </remarks>
    public bool Watched => _onlookers.Present;

    /// <summary>Whether a cycle is open now.</summary>
    /// <remarks>
    /// <para>
    /// What the status bar draws its badge and its button from, and the one
    /// thing a page has to be right about the instant a run is started.
    /// </para>
    /// <para>
    /// <strong>The whole cycle, not the searching half of it.</strong> The
    /// owner's ruling of 13 September 2026: a download and an encode a cycle
    /// asked for are still that cycle, so this stays true until maintenance has
    /// run — which is when there is nothing left in hand. Pressing Run again
    /// meanwhile is refused, and the two triggers that are not a button add
    /// their work to the open cycle instead.
    /// </para>
    /// </remarks>
    public bool Running
    {
        get
        {
            lock (_cycleLock)
            {
                return _open;
            }
        }
    }

    /// <summary>
    /// Starts a cycle in the background, and answers at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>F1.</strong> 0.3.4 awaited the cycle inside the HTTP request, so
    /// it ran on the caller's cancellation token — a browser tab closed after
    /// half an hour threw away twenty-nine minutes of work. The cycle is
    /// started on the plugin's own lifetime and the request only asks for it.
    /// </para>
    /// <para>
    /// The whole cycle now, not the search alone: feed, then search, then
    /// whatever they start, and maintenance when nothing is left in hand.
    /// </para>
    /// </remarks>
    /// <returns>Whether one was started, or false when one was already open.</returns>
    public bool StartRun()
    {
        // And since when, before anything is started: the push below is what
        // redraws every open page, and it should say "running since" straight
        // away. It used to be set inside the task, and the owner saw a Run
        // button still enabled on a run they had just pressed.
        _runStartedAt ??= DateTimeOffset.UtcNow;

        if (!Trigger("the owner pressed Run"))
        {
            _runStartedAt = null;

            return false;
        }

        // Now, and not before: the snapshot this sends says the cycle is
        // running, because by now it is.
        Moved();

        return true;
    }

    /// <summary>
    /// Cancels the running cycle, leaving the transfers alone.
    /// </summary>
    /// <remarks>
    /// Stopping a search is not stopping a download: what has already been
    /// handed to the torrent client keeps going, which is what the owner
    /// expects and what docs/08-ui.md says.
    /// </remarks>
    /// <returns>Whether there was one to stop.</returns>
    public bool StopRun()
    {
        CancellationTokenSource? cycle = _cycle;

        if (cycle is null)
        {
            return false;
        }

        try
        {
            cycle.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // It finished between the read and the cancel, which is a race a
            // button really runs.
            return false;
        }

        // And nothing more is added to it. A trigger that arrived while the
        // owner was deciding to stop would otherwise start the search again the
        // moment they did.
        lock (_cycleLock)
        {
            _again = false;
        }

        return true;
    }

    /// <summary>
    /// Looks for one episode now, outside the cadence.
    /// </summary>
    /// <remarks>
    /// The button an owner presses when something has just aired and they do
    /// not want to wait six hours. It answers at once and runs on the plugin's
    /// own lifetime, for the same reason <see cref="StartRun"/> does — and it
    /// refuses an episode this plugin is not tracking rather than claiming to
    /// search for one it has never heard of.
    /// </remarks>
    /// <returns>Whether a search was started.</returns>
    public async Task<bool> StartSearchAsync(EpisodeKey episode)
    {
        // On the plugin's own lifetime, including the look-up. Whether this
        // episode is worth searching for is part of starting the work, not part
        // of answering the caller — and a caller who has gone is exactly the
        // case where the work still has to start.
        IReadOnlyList<TrackedEpisode> tracked = await Tracked(Lifetime);

        if (!tracked.Any(one => one.Key == episode) || _running.Busy)
        {
            return false;
        }

        _ = Task.Run(() => CycleAsync(Lifetime, episode), CancellationToken.None);

        return true;
    }

    /// <summary>Pauses one transfer, keeping its pieces.</summary>
    /// <returns>Whether the client is holding it at all.</returns>
    public async Task<bool> PauseDownloadAsync(string infoHash, CancellationToken ct)
    {
        if (await ClientAsync(ct) is not BittorrentEngine engine || !await HoldsAsync(engine, infoHash, ct))
        {
            return false;
        }

        await engine.PauseAsync(infoHash, ct);
        await (await GrabsAsync(ct)).StateAsync(infoHash, GrabState.Paused, ct);

        return true;
    }

    /// <summary>Starts a paused transfer again, from what it has already.</summary>
    public async Task<bool> ResumeDownloadAsync(string infoHash, CancellationToken ct)
    {
        if (await ClientAsync(ct) is not BittorrentEngine engine || !await HoldsAsync(engine, infoHash, ct))
        {
            return false;
        }

        await engine.ResumeAsync(infoHash, ct);
        await (await GrabsAsync(ct)).StateAsync(infoHash, GrabState.Downloading, ct);

        return true;
    }

    /// <summary>
    /// Stops a transfer, forgets it, and puts its episodes back to missing.
    /// </summary>
    /// <remarks>
    /// The files go with it. The owner asked for this download to stop, and
    /// leaving half a file in the incomplete folder is disk nobody will ever
    /// account for.
    /// </remarks>
    public async Task<bool> CancelDownloadAsync(string infoHash, CancellationToken ct)
    {
        if (!await (await GrabsAsync(ct)).CancelledAsync(infoHash, DateTimeOffset.UtcNow, ct))
        {
            return false;
        }

        if (await ClientAsync(ct) is BittorrentEngine engine)
        {
            await engine.RemoveAsync(infoHash, deleteFiles: true, ct);
        }

        _journal.Failed(ActivityStage.Download, infoHash, "cancelled by hand");

        return true;
    }

    /// <summary>
    /// Takes on a torrent the owner found themselves.
    /// </summary>
    /// <remarks>
    /// Written down like any other grab, so the Downloads page shows it and it
    /// is staged the moment it finishes. It answers for no episode
    /// yet: what it turns out to be is the staging stage's business, and the
    /// dispatch says which file it could not place.
    /// </remarks>
    /// <returns>The hash it was taken on as, or why it was refused.</returns>
    public async Task<(string? InfoHash, string? Refusal)> AddTorrentAsync(string source, CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return (null, "No folders are configured, so there is nowhere to put a download.");
        }

        BittorrentEngine engine = await EngineAsync(settings, ct);

        try
        {
            TorrentHandle taken = await engine.AddAsync(
                new(source, [], settings.IncompleteFolder),
                ct);

            await (await GrabsAsync(ct)).RecordAsync(
                new(0, 0, 0),
                string.Empty,
                taken.Name ?? taken.InfoHash,
                "by hand",
                taken.InfoHash,
                source,

                // No episode. One added by hand answers for whatever it turns
                // out to hold, and saying it covers an episode nobody chose
                // would put that episode back to missing if it failed.
                [],
                DateTimeOffset.UtcNow,
                ct);

            return (taken.InfoHash, null);
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            return (null, refused.Message);
        }
    }

    /// <summary>The torrent client, when there is one to reach.</summary>
    private async Task<BittorrentEngine?> ClientAsync(CancellationToken ct)
    {
        return await ConfiguredAsync(ct) is Settings settings ? await EngineAsync(settings, ct) : null;
    }

    /// <summary>Whether the client really has this torrent.</summary>
    /// <remarks>
    /// Asked before anything is said to have happened. A page that showed a
    /// torrent pausing when nothing paused is one nobody can trust about
    /// anything else either.
    /// </remarks>
    private static async Task<bool> HoldsAsync(BittorrentEngine engine, string infoHash, CancellationToken ct)
    {
        return (await engine.StatusAsync(ct))
            .Any(one => string.Equals(one.InfoHash, infoHash, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Keeps every tracker the cycle came across, for every grab after it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The owner's decision, 20 August 2026: the default list is everything
    /// this plugin meets rather than something anybody types in. More trackers
    /// is a faster download, and the swarm one release was posted to is usually
    /// the swarm the next one is in.
    /// </para>
    /// <para>
    /// Saved only when it really grew. The settings go through the host's own
    /// store and a save that wrote the same list back every cycle would be a
    /// write every six hours saying nothing.
    /// </para>
    /// </remarks>
    public async Task KeepTrackersAsync(Settings settings, IReadOnlyList<string> seen, CancellationToken ct)
    {
        if (seen.Count == 0)
        {
            return;
        }

        IReadOnlyList<string> kept = TrackerBook.Learn(
            settings.Client.DefaultTrackers,
            seen,

            // Never the owner's own. A private tracker's announce address
            // carries their passkey, and this list travels with every grab.
            [.. settings.PrivateTrackers.Select(one => one.Host)]);

        if (kept.Count == settings.Client.DefaultTrackers.Count)
        {
            return;
        }

        settings.Client.DefaultTrackers = [.. kept];

        SaveResult saved = await Settings.SaveAsync(settings, ct);

        if (!saved.Saved)
        {
            _context?.Logger.LogWarning(
                "The trackers this cycle found could not be kept: {Reasons}",
                string.Join(" ", saved.Errors));
        }
    }

    /// <summary>
    /// Reads the library and writes down every episode that should have a file
    /// and has not.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The whole rule is in <see cref="MissingRefresh"/>: every show switched on
    /// with saved settings and a quality, every episode without a file, missing
    /// once it has aired. It derives and returns; the repository is what compares
    /// that against what is stored and keeps the plugin's own bookkeeping —
    /// attempts, last search — for the rows that survive.
    /// </para>
    /// <para>
    /// Both halves existed and neither had a caller. What was then the
    /// maintenance job was a <c>default: break;</c>, so on a real server every page said every
    /// episode of every show was on disk over a library with nearly two
    /// thousand that were not.
    /// </para>
    /// </remarks>
    private async Task RefreshAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is null)
        {
            return;
        }

        await DeriveMissingAsync(ct);
    }

    private async Task DeriveMissingAsync(CancellationToken ct)
    {
        MissingRefresh refresh = new(new HostLibrary(Context.Library), await AppliedAsync(ct), TimeProvider.System);

        IReadOnlyList<TrackedEpisode> derived = await refresh.DeriveAsync(ct);

        await (await EpisodesAsync(ct)).ReplaceAsync(derived, ct);
    }

    /// <summary>
    /// The housekeeping a cycle ends with, once nothing is left in hand.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It used to be scattered: the maintenance job's whole body was a refresh
    /// search already did before each of its four daily cycles, old
    /// refusals were pruned as a side effect of that refresh, and duplicate
    /// grab rows were cleared on the first transfers tick after a start behind
    /// a flag. Three pieces of periodic work, none of them here.
    /// </para>
    /// <para>
    /// The refresh stays, because it is what makes the pages true overnight
    /// without waiting on a search. Search keeps its own: a cycle needs a fresh
    /// missing list and must not wait for four in the morning.
    /// </para>
    /// </remarks>
    private async Task MaintainAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return;
        }

        await MaintainAsync(settings, ct);
    }

    private async Task MaintainAsync(Settings settings, CancellationToken ct)
    {
        await DeriveMissingAsync(ct);

        GrabRepository grabs = await GrabsAsync(ct);

        // The refusals nobody will read again. One is written for every release
        // every cycle considered and did not take: the owner's history held
        // 66,149 lines, 65,878 of them refusals, and the page stopped
        // answering. A fortnight is long enough to look back at why something
        // did not arrive.
        int gone = await grabs.PruneHistoryAsync(DateTimeOffset.UtcNow.AddDays(-14), ct);

        if (gone > 0)
        {
            Context.Logger.LogInformation("{Count} old refusals were cleared from the history.", gone);
        }

        // Downloads no grab answers for any more. A cancelled or pruned grab
        // used to leave its folder, its metadata and its resume file behind
        // with nothing left to ask for them: 8.6 GB of a cancelled season pack
        // sat in the owner's download folder for three days. Only what this
        // plugin wrote is recognised, so a folder the owner put there is left
        // where it is.
        // Asked for rather than read off the field. The housekeeping a start
        // owes runs on the first tick of any cadence, and the client is built
        // on first use — so at a start the field is still null and this was
        // skipped every single time, leaving the sweep to the four-o'clock
        // maintenance and nowhere else. The owner's 8.6 GB survived three
        // restarts that way.
        if (await ClientAsync(ct) is BittorrentEngine client)
        {
            IReadOnlyList<StoredDownload> every = await grabs.EveryAsync(ct);
            IReadOnlyList<string> swept = client.ForgetAbandoned([.. every.Select(one => one.InfoHash)]);

            foreach (string infoHash in swept)
            {
                Context.Logger.LogInformation(
                    "{Hash} was cleared from the download folder: no grab of this plugin's answers for it.",
                    infoHash);
            }
        }
    }

    /// <summary>
    /// The housekeeping a start owes, done once, whichever asks for it first.
    /// </summary>
    /// <remarks>
    /// <para>
    /// What the library holds is derived rather than stored, and a plugin that
    /// only re-derived it on its six-hourly cycle carried whatever the last run
    /// left behind — including, on 24 August 2026, shows a broken build had put
    /// there that the owner does not have. A restart settles that within the
    /// minute rather than by tea time.
    /// </para>
    /// <para>
    /// It used to be the first transfers tick that did this, which made one
    /// tick of one cadence unlike all the others. What is special is the start,
    /// not the tick, so it is done here — and if the first tick after a start
    /// happens to be maintenance, the housekeeping runs twice that once. Every
    /// part of it is idempotent, and a special case to save the second pass
    /// would be the special case this removed.
    /// </para>
    /// </remarks>
    private async Task SettleOnceAsync(CancellationToken ct)
    {
        if (Volatile.Read(ref _settled) != 0)
        {
            return;
        }

        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            // Not settled, and not marked as settled. A plugin the owner has
            // not configured yet has nowhere to put anything and nothing to
            // settle, and it must still do this on the first tick after they
            // do configure it.
            return;
        }

        if (Interlocked.Exchange(ref _settled, 1) != 0)
        {
            // Something else asked at the same moment and got there first.
            return;
        }

        await MaintainAsync(settings, ct);
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
    /// the same semaphore as the migration because a cycle and a page can both
    /// arrive at once on a plugin that has just loaded.
    /// </remarks>
    private async Task<(Chain Chain, Settings Settings)?> ChainAsync(CancellationToken ct)
    {
        if (await ConfiguredAsync(ct) is not Settings settings)
        {
            return null;
        }

        BittorrentEngine engine = await EngineAsync(settings, ct);

        // Everything that touches the database is asked for **before** the lock
        // is taken. Migrating takes this same semaphore, and SemaphoreSlim is
        // not reentrant: asking for the database while holding it is the plugin
        // waiting on itself, for ever, on the first tick after a restart. There
        // is no exception and no log line — the tick simply never returns, and
        // because no cadence may overlap itself that one never runs again for
        // as long as the server is up.
        Store database = await DatabaseAsync(ct);
        SourceLedgerRepository ledger = await LedgerAsync(ct);

        await _migrating.WaitAsync(ct);

        try
        {
            _chain ??= new(
                Context,
                _journal,
                Shipped(),
                engine: engine,
                ledger: ledger);
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
    /// migration, because a cycle, a transfers pass and a page can all arrive at
    /// once on a plugin that has only just loaded.
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
                    TimeSpan.FromMinutes(settings.Client.StallMinutes),
                    settings.Client.MaxConcurrentDownloads,
                    new SeedLimit(settings.Client.SeedRatio, TimeSpan.FromHours(settings.Client.SeedHours)),
                    settings.Client.MaxDownloadRate,
                    settings.Client.MaxUploadRate,

                    // Always asked, and the answer never drawn. The switch
                    // that used to guard this was the owner's way of silencing
                    // a notice that should not have existed: a refusal says the
                    // router would not open the port by itself, which on a
                    // hand-forwarded port means nothing at all. It is logged
                    // and no page reports it, so there is nothing left to
                    // switch off.
                    new PortMapping([new UpnpMapper(_trackerHttp), new NatPmpMapper()]),
                    _journal,
                    Context.Logger,
                    new SocketTrackerTransport(_trackerHttp),
                    new SocketPeerDialler(
                        SocketPeerDialler.DefaultPatience,
                        settings.Client.Encryption switch
                        {
                            EncryptionPolicy.Required => PeerEncryption.Required,
                            EncryptionPolicy.Disabled => PeerEncryption.Disabled,
                            _ => PeerEncryption.Allowed,
                        }),
                    resume: new ResumeKeeper(
                        settings.IncompleteFolder,
                        TimeSpan.FromSeconds(settings.Client.ResumeIntervalSeconds),
                        TimeProvider.System));

                _engine.Start();

                // When something a page shows has changed, and only then. Not
                // on a rhythm: the owner does not want the whole view every
                // tick, they want to be told when something moved. And not
                // only when bytes move, which is what this used to ask — a
                // peer arriving, a seed arriving, a peer choking us, a torrent
                // stalling are all changes to what is on the screen, and none
                // of them shifts a byte.
                _heartbeat = new(
                    () => _engine?.Drawn,

                    // And only while it is holding something. Without this the
                    // client was read once a second for the life of the server,
                    // whether it had a torrent or not.
                    () => _engine?.Watching ?? false,
                    Moved,
                    Context.Logger);

                // The client moving wakes a watch that went to rest over a page
                // with nothing changing on it. A stalled torrent starting again
                // is the one somebody was staring at. S11-29.
                _engine.Stirred += _onlookers.Stirred;

                // A download finishing is what starts staging it. The client
                // raised this from the day it could say so, and nothing here
                // listened: the transfers job ticking every minute was what
                // joined them, and when it went a finished download would have
                // sat in the incomplete folder for ever.
                _engine.Completed += _ => StartTransfers();

                // And the client giving one up. The pass is what fails the grab,
                // blacklists the release and takes the torrent out of the
                // client; without it a dropped torrent is held for ever, and the
                // cycle with it.
                _engine.GaveUp += _ => StartTransfers();

                // And a page already open when the client was built is watched
                // from now, rather than from the next time it is fetched.
                if (_onlookers.Present)
                {
                    _heartbeat.StartBeating();
                }
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
        // Every page carries the plugin's own navigation, put on here rather
        // than by each view: six of the eight are mounted nowhere in the
        // server's navigation and were reachable only by typing an address.
        PluginView view = Pages.WithNavigation(await PageAsync(request, ct), request.Route);

        // What this page was drawn with, taken straight after it was, so the
        // heartbeat pushes only what differs from it. Read off a client that
        // already exists and never by building one: opening a page must not
        // start a torrent client on a plugin that has not needed one yet.
        if (_engine is BittorrentEngine client)
        {
            _heartbeat?.Shown(client.Drawn);
        }

        // The proof somebody is looking, and the only one there is. The hub adds
        // a connection to this plugin's group and tells the plugin nothing; but a
        // page that is open fetches itself again on every push, so every fetch
        // is somebody with this plugin in front of them. After the page is drawn,
        // so the first beat is never compared with what it was before.
        _onlookers.Looked();

        return view;
    }

    private async Task<PluginView> PageAsync(PluginViewRequest request, CancellationToken ct)
    {
        // Rendered per request from the current state, never from a tree held
        // between requests: a cached page goes stale silently, and the page
        // most worth trusting is the one saying what is happening now.
        PluginRouteMatch? match = Pages.Routes.Resolve(request.Route);

        switch (match?.Route.Name)
        {
            case "settings":
                // Names, never values. The page is given the keys that exist
                // and has no route to what is behind them.
                return SettingsView.Render(
                    await Settings.LoadAsync(ct),
                    await Settings.SecretsSetAsync(ct),
                    [],

                    // What is known about the port, which on an idle server is
                    // nothing. Read off the field rather than through Engine():
                    // opening the Settings page must not start a torrent client
                    // that is not running, and a client that is not running has
                    // proved nothing either way.
                    _engine?.PortCondition ?? PortState.Unknown,
                    ShowAdvanced);

            case Pages.ShowSettingsName:
                return await ShowSettingsPageAsync(match!.Param("id"), ct);

            case Pages.LibraryPreferencesName:
                return await LibraryPreferencesPageAsync(match!.Param("id"), ct);

            case Pages.LibraryShowsName:
                return await OverviewAsync(
                    match!.Param("id"),
                    int.TryParse(match.Param("page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int page) ? page : 1,
                    ct);

            case "queue":
                return QueueView.Render(await Tracked(ct));

            case "downloads":
                return DownloadsView.Render(await DownloadRowsAsync(ct));

            case "sources":
                return SourcesView.Render(
                    await SourceReportsAsync(ct),
                    DateTimeOffset.UtcNow,

                    // The shipped catalogue, so every source has a switch, and
                    // the settings so each switch knows which way it is.
                    Shipped(),
                    await Settings.LoadAsync(ct),

                    // Only which secrets exist, never their values: the page
                    // draws that a key is set and has nothing it could leak.
                    await Settings.SecretsSetAsync(ct),
                    ShowAdvanced);

            case "skipped":
            case Pages.SkippedPageName:
                // A page of them, not all of them: one refusal is written for
                // every release every cycle considered and did not take, and
                // the owner's history held 65,878 of them. Anything that is not
                // a number is the first page rather than an error.
                return SkippedView.Render(await (await GrabsAsync(ct)).SkippedAsync(
                    int.TryParse(match!.Param("page"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int skippedPage) ? skippedPage : 1,
                    SkippedView.PageSize,
                    ct));

            case "history":
                return HistoryView.Render(
                    [.. (await (await GrabsAsync(ct)).HistoryAsync(ct)).Select(Line)]);

            case "activity":
                await LastRunAsync(ct);

                ActivitySnapshot activity = _journal.Snapshot();

                // What the client holds only while a run is going: that is the
                // only time the Download stage is drawn, and asking the client
                // for a page that will not show it is work for nothing.
                return ActivityView.Render(
                    activity,
                    CurrentCycle(),
                    activity.Run is null ? null : await DownloadRowsAsync(ct));

            default:
                await LastRunAsync(ct);

                return await OverviewAsync(null, 1, ct);
        }
    }

    /// <summary>What applies to every show a run has an episode of, read once for the run.</summary>
    private async Task<SettingsByShow> SettingsByShowAsync(IReadOnlyList<TrackedEpisode> tracked, CancellationToken ct)
    {
        HashSet<int> wanted = [.. tracked.Select(episode => episode.Key.ShowId)];

        return await (await AppliedAsync(ct)).ForShowsAsync(
            (await new HostLibrary(Context.Library).GetShowsAsync(ct)).Where(show => wanted.Contains(show.Id)),
            ct);
    }

    /// <summary>What applies to each show, read from the saved settings on every call.</summary>
    private async Task<AppliedSettings> AppliedAsync(CancellationToken ct)
    {
        return new(await ShowSettingsAsync(ct), await LibraryPreferencesAsync(ct));
    }

    /// <summary>
    /// The overview, or one library's page of shows: every show of every tv and anime library, with what
    /// was saved for it and what applies.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The count is the library's own, not the plugin's.</strong> It used to come from the
    /// episodes the plugin tracks, which are only those of shows switched on, so every row of a library
    /// nobody had switched on yet read "not counted" — the owner's report of 16 September 2026, and the
    /// information was there all along. Each show's episodes are read once here and counted
    /// (<see cref="ShowFacts"/>).
    /// </para>
    /// <para>
    /// <strong>And a show the library holds no file of is not listed</strong>, unless the owner switched
    /// it on. The server keeps a row for every show it ever identified — twelve of the owner's
    /// sixty-nine — and listing those read as recommendations for programmes they do not have.
    /// </para>
    /// </remarks>
    private async Task<PluginView> OverviewAsync(string? onlyLibraryId, int page, CancellationToken ct)
    {
        (IReadOnlyList<Library> libraries, IReadOnlyList<Show> shows, string? unread) = await ShelvesAsync(ct);

        if (unread is not null)
        {
            return Unread(unread);
        }

        IReadOnlyDictionary<int, ShowSettings> saved = await (await ShowSettingsAsync(ct)).AllAsync(ct);
        LibraryPreferencesRepository preferences = await LibraryPreferencesAsync(ct);

        // One library object for the whole page, so a show's episodes are asked
        // for once however many times they are wanted while it is drawn.
        LibraryThisTick library = new(new HostLibrary(Context.Library));
        DateOnly today = DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime);

        List<LibraryListing> listings = [];

        foreach (Library shelf in libraries)
        {
            LibraryPreferences prefs = await preferences.ForAsync(shelf.Id, ct);
            List<ShowListing> rows = [];

            foreach (Show show in shows.Where(one => one.LibraryId == shelf.Id))
            {
                ShowSettings settings = saved.GetValueOrDefault(show.Id) ?? new ShowSettings(show.Id);
                EffectiveSettings applied = EffectiveSettings.Of(settings, prefs);

                ShowFacts facts = ShowFacts.Of(
                    await library.GetEpisodesAsync(show.Id, ct),
                    today,
                    applied.Specials);

                // Nothing of it on disk and nobody asked for it: a row the
                // server wrote on a guess, and not the owner's show.
                if (!facts.Held && !settings.SwitchedOn)
                {
                    continue;
                }

                rows.Add(new(show, settings, applied, facts.Missing));
            }

            listings.Add(new(shelf, prefs, rows));
        }

        return OverviewView.Render(CurrentCycle(), listings, onlyLibraryId, page);
    }

    /// <summary>The settings form of one show, or a page saying there is no such show.</summary>
    private async Task<PluginView> ShowSettingsPageAsync(string? id, CancellationToken ct)
    {
        (_, IReadOnlyList<Show> shows, string? unread) = await ShelvesAsync(ct);

        if (unread is not null)
        {
            return Unread(unread);
        }

        if (!int.TryParse(id, NumberStyles.Integer, CultureInfo.InvariantCulture, out int showId)
            || shows.FirstOrDefault(show => show.Id == showId) is not Show show)
        {
            return Said($"No show with the id {id} is in a tv or anime library.");
        }

        return ShowSettingsView.Render(
            show,
            await (await ShowSettingsAsync(ct)).ForAsync(show.Id, ct),
            await (await LibraryPreferencesAsync(ct)).ForAsync(show.LibraryId, ct));
    }

    /// <summary>The preferences form of one library, or a page saying there is no such library.</summary>
    private async Task<PluginView> LibraryPreferencesPageAsync(string? id, CancellationToken ct)
    {
        (IReadOnlyList<Library> libraries, _, string? unread) = await ShelvesAsync(ct);

        if (unread is not null)
        {
            return Unread(unread);
        }

        if (libraries.FirstOrDefault(library => library.Id == id) is not Library library)
        {
            return Said($"No tv or anime library has the id {id}.");
        }

        return LibraryPreferencesView.Render(library, await (await LibraryPreferencesAsync(ct)).ForAsync(library.Id, ct));
    }

    /// <summary>The tv and anime libraries and their shows, or why they could not be read.</summary>
    /// <remarks>
    /// A server that does not let this plugin read its library, or one that fails while it answers, is a
    /// page that says so. The overview is the landing page, and a landing page that threw would be a blank
    /// screen with nothing on it to act on.
    /// </remarks>
    private async Task<(IReadOnlyList<Library> Libraries, IReadOnlyList<Show> Shows, string? Unread)> ShelvesAsync(CancellationToken ct)
    {
        try
        {
            HostLibrary library = new(Context.Library);

            return (await library.GetLibrariesAsync(ct), await library.GetShowsAsync(ct), null);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            return ([], [], wrong.Message);
        }
    }

    private static PluginView Unread(string reason)
    {
        return Said($"The libraries could not be read: {reason}");
    }

    /// <summary>A page that says one thing, for an address that names nothing the plugin can draw.</summary>
    private static PluginView Said(string said)
    {
        return new()
        {
            Layout = PluginLayout.Wide,
            Components = [Ui.Text("said", said)],
        };
    }

    /// <summary>What the owner saved per show.</summary>
    public async Task<ShowSettingsRepository> ShowSettingsAsync(CancellationToken ct)
    {
        return new(await DatabaseAsync(ct));
    }

    /// <summary>What the owner saved per library.</summary>
    public async Task<LibraryPreferencesRepository> LibraryPreferencesAsync(CancellationToken ct)
    {
        return new(await DatabaseAsync(ct));
    }

    /// <summary>Switches a show on or off from its row on the overview, and tells the pages.</summary>
    public async Task SwitchShowAsync(int showId, bool on, CancellationToken ct)
    {
        await (await ShowSettingsAsync(ct)).SwitchAsync(showId, on, ct);

        Moved();
    }

    /// <summary>Saves a show's settings form, or refuses it with the fields named and saves nothing.</summary>
    public async Task<IReadOnlyList<string>> SaveShowSettingsAsync(
        int showId,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken ct)
    {
        ShowSettingsRepository shows = await ShowSettingsAsync(ct);

        (ShowSettings settings, IReadOnlyList<string> refused) = ShowSettingsEdit.Show(await shows.ForAsync(showId, ct), fields);

        if (refused.Count == 0)
        {
            await shows.SaveAsync(settings, ct);

            Moved();
        }

        return refused;
    }

    /// <summary>Saves a library's preferences form, or refuses it with the fields named and saves nothing.</summary>
    public async Task<IReadOnlyList<string>> SaveLibraryPreferencesAsync(
        string libraryId,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken ct)
    {
        LibraryPreferencesRepository libraries = await LibraryPreferencesAsync(ct);

        (LibraryPreferences preferences, IReadOnlyList<string> refused) = ShowSettingsEdit.Library(await libraries.ForAsync(libraryId, ct), fields);

        if (refused.Count == 0)
        {
            await libraries.SaveAsync(preferences, ct);

            Moved();
        }

        return refused;
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
                settings.IncompleteFolder)
            {
                EncodeFailure = _transfers?.FailureOf(one.InfoHash),
            }),
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

    private async Task<RunRepository> RunsAsync(CancellationToken ct)
    {
        await EpisodesAsync(ct);

        return _runs ?? throw new InvalidOperationException("The plugin has not been initialised, so it has no store yet.");
    }

    /// <summary>How the last run ended, read from the store the first time it is wanted.</summary>
    private async Task LastRunAsync(CancellationToken ct)
    {
        if (_lastRunRead)
        {
            return;
        }

        LastRun? stored = await (await RunsAsync(ct)).LastAsync(ct);

        // A run that ended while this was reading is newer than anything on
        // disk, and it wins.
        _lastRun ??= stored;
        _lastRunRead = true;
    }

    /// <summary>
    /// Keeps how a run ended: on the page at once, and on disk for the next
    /// start.
    /// </summary>
    private async Task RememberAsync(LastRun run)
    {
        _lastRun = run;
        _lastRunRead = true;

        try
        {
            // Not the run's token: a stopped run arrives here with it
            // cancelled, and the stop is exactly what has to be written down.
            await (await RunsAsync(CancellationToken.None)).RecordAsync(run, CancellationToken.None);
        }
        catch (Exception unwritten) when (unwritten is not OperationCanceledException)
        {
            // The page already has it. What is lost is only the next start's
            // memory of it, and that is not worth taking the run's ending down.
            _context?.Logger.LogWarning(unwritten, "How the run ended could not be written down: {Reason}", unwritten.Message);
        }
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

    /// <summary>Something moved, so the open pages are due a push.</summary>
    /// <remarks>
    /// Also called where the cycle itself starts and stops, because those move
    /// the status bar without recording anything in the journal — and a page
    /// still saying "Running" after a cycle has finished is the same fault in
    /// a smaller place.
    /// </remarks>
    private void Moved()
    {
        _live?.Changed();
    }

    /// <summary>What adds a show the owner does not have, having said whether it can.</summary>
    private ShowImport Imports()
    {
        ShowImport imports = new(Context.Services, Context.Logger);

        imports.Ready();

        return imports;
    }

    /// <summary>
    /// What the status bar says about the search cycle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Since when a run is going, and how and when the last one ended — kept
    /// in the store, so a restart does not turn it into "never run".
    /// </para>
    /// <para>
    /// The next run is read from <see cref="_clock"/>'s own record of when the
    /// last cycle finished, never from a job's cron string: the host's job starts
    /// nothing, and the clock is what starts a cycle. <see cref="_nextCycleDue"/>
    /// is set whenever the clock is wound and simply read here, because this
    /// method has to stay synchronous for <see cref="LiveSnapshot"/>'s callback —
    /// before the clock is first wound it is null, and the page says the time is
    /// not known rather than inventing one.
    /// </para>
    /// </remarks>
    private CycleStatus CurrentCycle()
    {
        return new(
            _running.Busy,
            _lastRun?.EndedAt,
            _nextCycleDue)
        {
            StartedAt = _running.Busy ? _runStartedAt : null,
            LastEnd = _lastRun?.How,
        };
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

        // Before the engine, which it reads on every tick.
        _heartbeat?.Dispose();
        _onlookers.Dispose();
        _says?.Dispose();
        _scanning?.Dispose();
        _loaded?.Dispose();

        lock (_cycleLock)
        {
            _due?.Dispose();
            _due = null;
        }

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
    /// Says something once, however many times it comes up.
    /// </summary>
    /// <remarks>
    /// A plugin with no folders set is asked for them on every cycle, every
    /// transfers pass and every page, and a line each time is a line nobody reads
    /// — which is how a message that mattered went unnoticed in 0.3.4's log.
    /// </remarks>
    private void SayOnce(ref int said, string message)
    {
        if (Interlocked.Exchange(ref said, 1) == 0)
        {
            _context?.Logger.LogInformation("{Message}", message);
        }
    }

    /// <summary>
    /// Says once, in the server's own log, which version is running.
    /// </summary>
    /// <remarks>
    /// Once, not once per tick: a line every time the host ticks is a line
    /// nobody reads. It answers the question a deploy leaves
    /// open — a plugin's assembly is held open by a running server, so a copy
    /// onto one that was not stopped fails and the old build stays, which looks
    /// exactly like a deploy that worked (docs/01-plugin.md § Deploying).
    /// </remarks>
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
