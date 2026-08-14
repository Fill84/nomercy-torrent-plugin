using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Views;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader;

/// <summary>
/// The plugin the server loads: its identity, its four cadences and its pages.
/// </summary>
public sealed class TorrentDownloaderPlugin : IPlugin, IScheduledTaskPlugin, IUiPlugin
{
    private readonly CancellationTokenSource _lifetime = new();
    private IPluginContext? _context;
    private int _announced;
    private bool _disposed;

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
    }

    /// <summary>
    /// The entry point of a host that registered <see cref="CronExpression"/>
    /// rather than <see cref="Jobs"/>, so it runs what that expression names.
    /// </summary>
    public Task ExecuteAsync(CancellationToken ct = default)
    {
        return ExecuteAsync(JobNames.Transfers, ct);
    }

    public Task ExecuteAsync(string jobName, CancellationToken ct = default)
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

        // Every cadence's work arrives in its own slice: transfers in S6-02,
        // feed in S3-02, search in S4-04, maintenance in S1-02.
        return Task.CompletedTask;
    }

    public Task<PluginView> GetViewAsync(PluginViewRequest request, CancellationToken ct)
    {
        return Task.FromResult(Pages.Loaded());
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
        _lifetime.Dispose();
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
