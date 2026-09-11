using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Solver;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Everything that talks to the outside, assembled once.
/// </summary>
/// <remarks>
/// <para>
/// The catalogue, the host gate, the grants, the browser, the solver, the
/// readers and the pool, built together and kept for the plugin's life. Two
/// earlier slices deliberately left their cadence unwired and said so, because
/// half a chain reports failures the owner cannot act on: a fetch with no grant
/// refuses every host, and a gated source with no browser is a source that
/// looks dead.
/// </para>
/// <para>
/// One <see cref="HostGate"/> and one <see cref="Browser"/> for the process, or
/// two cadences ticking at once would each keep their own pace against the same
/// host and each start their own Chrome.
/// </para>
/// </remarks>
public sealed class Chain : IAsyncDisposable
{
    private readonly IPluginContext _context;
    private readonly ILogger _logger;
    private readonly IActivityJournal _journal;
    private readonly HostGate _gate;
    private readonly Readers _readers = Readers.Shipped();
    private readonly ClearanceStore _clearances = new();
    private readonly HttpClient _http;
    private readonly Browser _browser;
    private readonly PuppeteerTabs _tabs;
    private readonly BrowserSolver _solver;
    private readonly INamePool _pool;
    private readonly ISourceLedger? _ledger;
    private readonly ITorrentEngine? _engine;

    /// <summary>
    /// What a request calls itself.
    /// </summary>
    /// <remarks>
    /// A real browser's, because half these sites answer a client that names
    /// itself as a robot with a challenge — and the plugin then spends a
    /// browser solving a challenge it caused.
    /// </remarks>
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    public Chain(
        IPluginContext context,
        IActivityJournal journal,
        INamePool pool,
        IReadOnlyList<SourceDefinition> shipped,
        HttpMessageHandler? handler = null,
        ITorrentEngine? engine = null,
        ISourceLedger? ledger = null)
    {
        _context = context;
        _logger = context.Logger;
        _journal = journal;
        _pool = pool;
        _engine = engine;
        _ledger = ledger;
        _gate = new(TimeProvider.System);
        Shipped = shipped;

        _http = handler is null ? new() : new(handler);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", UserAgent);

        _browser = new(
            new BrowserInstall(Path.Combine(context.DataFolderPath, "browser"), new PuppeteerBrowserDownloader(), _logger),
            new HiddenStages(_logger),
            _logger);

        _tabs = new(_browser, _logger);
        _solver = new(_tabs, _logger);
    }

    /// <summary>The sources that ship, read from beside the assembly.</summary>
    public IReadOnlyList<SourceDefinition> Shipped { get; }

    /// <summary>
    /// The catalogue as it stands now: shipped, plus the owner's own, minus
    /// what they switched off.
    /// </summary>
    /// <remarks>
    /// Built per call rather than kept, because the owner can change it between
    /// two ticks and a cadence running yesterday's catalogue would ask a source
    /// that was switched off this morning.
    /// </remarks>
    public SourceCatalogue Catalogue(Settings settings)
    {
        return Catalogue(Shipped, settings);
    }

    /// <summary>
    /// The same catalogue, without a chain to build it with.
    /// </summary>
    /// <remarks>
    /// A page that lists the sources needs the catalogue and none of the rest
    /// of the chain: rendering it must not start a browser or ask the server
    /// for a grant.
    /// </remarks>
    public static SourceCatalogue Catalogue(IReadOnlyList<SourceDefinition> shipped, Settings settings)
    {
        return SourceCatalogue.Build(
            shipped,
            [.. settings.Indexers.Select(Owned)],
            settings.DisabledDefaultSources);
    }

    /// <summary>
    /// How long this source is being left between asks, as the gate has it now.
    /// </summary>
    /// <remarks>
    /// The gate's own figure, not the configured one: a refusal widens it and a
    /// success halves it again, and the widened figure is what the Sources page
    /// has to say — a site being left alone for fifteen minutes after refusing
    /// is not the same as one on its ordinary fifteen-second pace.
    /// </remarks>
    public TimeSpan IntervalFor(SourceDefinition source)
    {
        return source.Hosts.FirstOrDefault() is string host
            ? _gate.IntervalFor(host)
            : TimeSpan.FromSeconds(source.MinimumIntervalSeconds);
    }

    /// <summary>
    /// Asks the server for every host the owner's own sources reach, and paces
    /// every host in the catalogue.
    /// </summary>
    /// <remarks>
    /// <strong>C2.</strong> Shipped hosts are in the manifest and granted by
    /// installing the plugin; the owner's own are not, and 0.3.4 asked for
    /// nothing at all on a default install while searching the whole shipped
    /// catalogue.
    /// </remarks>
    public async Task PrepareAsync(Settings settings, CancellationToken ct)
    {
        SourceCatalogue catalogue = Catalogue(settings);

        foreach (SourceDefinition source in catalogue.Enabled)
        {
            foreach (string host in source.Hosts)
            {
                _gate.Configure(host, TimeSpan.FromSeconds(source.MinimumIntervalSeconds));
            }
        }

        // Every source the pipeline will actually ask, not only the ones the
        // owner typed in.
        //
        // C2, and it had already happened once. This asked for
        // settings.Indexers alone while the search reached the shipped
        // catalogue too, so on a default install — where the owner has added
        // none — nothing was ever asked for. The dashboard had no request to
        // show, no host was ever granted, and every shipped source refused
        // itself with "the server has not granted access", which reads exactly
        // like the sites turning us away.
        //
        // Declaring a host in the manifest is not being granted it: the
        // manifest says what the plugin may ask for, and the owner still says
        // yes once per host. They cannot say yes to a question nobody asked.
        await new HostGrants(_context.Grants, _logger).RequestAsync(
            [.. catalogue.Enabled, .. settings.Indexers.Select(Owned)],
            ct);
    }

    /// <summary>
    /// Clears every challenge the run will meet, before the run starts.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner's design, 11 September 2026.</strong> Chrome exists to
    /// get past Cloudflare and for nothing else. So every source and indexer
    /// whose address sits behind a challenge is cleared first, in one pass; the
    /// cookies that earns go into the clearance store; the browser is closed;
    /// and the whole run then goes over ordinary HTTP with no browser at all.
    /// </para>
    /// <para>
    /// Solved together rather than one at a time as each is first needed: a
    /// challenge met halfway through a cycle is a browser started halfway
    /// through a cycle, and the owner watched ten Chrome processes sitting on
    /// their server because of it.
    /// </para>
    /// <para>
    /// A host that will not clear is not a failure here. It is left without a
    /// clearance and meets its challenge the ordinary way when it is asked,
    /// which is one browser rather than none and still says so in the journal.
    /// </para>
    /// </remarks>
    public async Task ClearTheWayAsync(Settings settings, CancellationToken ct)
    {
        SourceCatalogue catalogue = Catalogue(settings);

        Uri[] behindAChallenge =
        [
            .. catalogue.Enabled
                .Where(source => source.SearchAddressGated || source.Gated)
                .Select(source => source.SearchAddress ?? source.Url)
                .Select(address => new Uri(address.Replace("{query}", "a", StringComparison.Ordinal)))

                // One per host: a clearance is issued to a host and not to a
                // path, so a second address on the same host is a challenge
                // already cleared.
                .GroupBy(address => address.Host, StringComparer.OrdinalIgnoreCase)
                .Select(perHost => perHost.First()),
        ];

        if (behindAChallenge.Length == 0)
        {
            return;
        }

        _logger.LogInformation(
            "Looking at {Count} hosts that may challenge us, before the run, so the run itself needs no browser.",
            behindAChallenge.Length);

        // Said on the page, not only in the log. This is the first thing a run
        // does and it can take a minute; a dashboard with nothing on it is a
        // plugin that looks asleep.
        string pass = $"{behindAChallenge.Length} sites that may need a browser";

        _journal.Started(ActivityStage.Clearance, pass);

        // Asked over plain HTTP first, with no solver behind it, purely to find
        // out which of them really challenges us today. The catalogue's gated
        // flag is what a site did when it was written down; this is what it
        // does now, and a site that has stopped challenging costs no browser
        // at all.
        ChallengeAwareFetch asking = new(_http, _gate, _context.Grants, _clearances);

        Uri[] unsettled = [.. behindAChallenge.Where(one => _clearances.For(one.Host) is null)];

        bool[] challenged = await Task.WhenAll(unsettled.Select(async address =>
        {
            FetchResult plainly = await asking.GetAsync(address, gated: false, ct);

            return plainly.Failure?.Outcome == FetchOutcome.Challenged;
        }));

        Uri[] needing = [.. unsettled.Where((_, at) => challenged[at])];

        foreach (Uri settled in unsettled.Where((_, at) => !challenged[at]))
        {
            _logger.LogDebug("{Host} answered without a challenge, so it needs no browser.", settled.Host);
            _journal.Finished(ActivityStage.Clearance, settled.Host, "answered without a challenge");
        }

        if (needing.Length == 0)
        {
            _journal.Finished(ActivityStage.Clearance, pass, "none of them needed a browser");

            return;
        }

        _logger.LogInformation(
            "Clearing {Count} challenges together, so the run itself needs no browser.",
            needing.Length);

        // **All of them at once, in one browser.** The owner's rule of
        // 11 September 2026. Solved one after another, each tab closes behind
        // itself and takes the browser with it — measured that day as Chrome
        // going up and down five times in a row, which is five cold starts and
        // five challenges met from nothing.
        await Task.WhenAll(needing.Select(async address =>
        {
            _journal.Started(ActivityStage.Clearance, address.Host, "clearing its challenge in the browser");

            try
            {
                Clearance? earned = await _solver.SolveAsync(address, ct);

                if (earned is not null)
                {
                    _clearances.Keep(address.Host, earned);
                    _journal.Finished(ActivityStage.Clearance, address.Host, "cleared, and its cookie kept");
                }
                else
                {
                    _logger.LogDebug(
                        "{Host} cleared without a cookie, so its pages come from the browser.",
                        address.Host);

                    _journal.Finished(
                        ActivityStage.Clearance,
                        address.Host,
                        "cleared without a cookie, so its pages come from the browser");
                }
            }
            catch (Exception wrong) when (wrong is not OperationCanceledException)
            {
                // One host is one host. What it costs is that host meeting its
                // challenge again when it is really asked.
                _logger.LogDebug(wrong, "The challenge on {Host} could not be cleared up front.", address.Host);
                _journal.Failed(ActivityStage.Clearance, address.Host, wrong.Message);
            }
        }));

        // Everything that could be cleared has been, so the browser has nothing
        // left to do until the next run.
        await _tabs.CloseAllAsync(ct);

        _journal.Finished(
            ActivityStage.Clearance,
            pass,
            $"{needing.Length} cleared, and the browser closed");
    }

    /// <summary>Reads every feed into the pool.</summary>
    public Harvest Harvest(Settings settings)
    {
        return new(Catalogue(settings), Fetch(), _readers, _pool, _journal, TimeProvider.System, _ledger);
    }

    /// <summary>The whole search chain: names, indexers, decision, grab.</summary>
    /// <param name="settings">What the owner will accept.</param>
    /// <param name="written">
    /// Where each decision is written the moment it is made, or null when
    /// nothing is keeping them.
    /// </param>
    public SearchCycle Search(Settings settings, ICycleJournal? written = null)
    {
        SourceCatalogue catalogue = Catalogue(settings);
        IFetch fetch = Fetch();

        return new(
            new NameResolve(catalogue, fetch, _readers, _pool, _journal, TimeProvider.System),
            // The solver again, as the thing that can post from inside the
            // session that loaded the page. TorrentBay names its torrents to
            // nothing else, and while nobody passed this its every row was a
            // dead end - the best-seeded dead end on the page.
            new Find(catalogue, fetch, _readers, _journal, _ledger, TimeProvider.System, _solver),
            _journal,

            // Through the grab, which checks there is room before anything is
            // handed over. A cycle that reached the client directly went round
            // that check, and a torrent that fills the disk takes the media
            // server with it.
            _engine is null ? null : new Grab(_engine, new DiskSpace(), _journal),
            written);
    }

    /// <summary>
    /// A run has ended: every tab closed and the browser taken down.
    /// </summary>
    /// <remarks>
    /// A tab is kept per site and reused while a run lasts, so a gated site
    /// meets its challenge once for the whole cycle instead of once per page.
    /// The run's end is the only moment anything knows it is safe to let go,
    /// and the owner asked on 11 September 2026 that it be let go then: ten
    /// Chrome processes sitting on a finished server is two hundred megabytes
    /// held for nothing.
    /// </remarks>
    public Task RunEndedAsync(CancellationToken ct)
    {
        return _tabs.CloseAllAsync(ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _tabs.DisposeAsync();

        // After the tabs: closing the desktop out from under a window still on
        // it is the one order that can leave a stray process with nowhere to be.
        _browser.Dispose();
        _http.Dispose();
    }

    /// <summary>
    /// The fetch every stage uses: the grant, the gate, plain HTTP, and the
    /// browser when there is no other way.
    /// </summary>
    private ChallengeAwareFetch Fetch()
    {
        return new(_http, _gate, _context.Grants, _clearances, _solver, _solver);
    }

    /// <summary>An indexer the owner added, as a source like any other.</summary>
    private static SourceDefinition Owned(OwnIndexer indexer)
    {
        return new(indexer.Name, indexer.Kind, indexer.Address)
        {
            Priority = indexer.Priority,
            MinimumIntervalSeconds = indexer.MinimumIntervalSeconds,
            Enabled = indexer.Enabled,
        };
    }
}
