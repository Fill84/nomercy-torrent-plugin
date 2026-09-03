using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The host, as far as a test is concerned.
/// </summary>
/// <remarks>
/// Everything the plugin has not needed yet throws when it is touched, naming
/// itself. A null would come back as a NullReferenceException three layers into
/// the code under test, which says nothing about which part of the host was
/// missing; a half-built stub is worse still, because the test then passes
/// against behaviour the server does not have.
/// </remarks>
public sealed class FakePluginContext : IPluginContext
{
    public CapturingLogger Log { get; } = new();

    public ILogger Logger => Log;

    /// <summary>
    /// Provided, unlike most of the host: the plugin reaches for the hub while
    /// it initialises, so every test that initialises one needs it.
    /// </summary>
    public FakeHub Pushes { get; } = new();

    public IPluginHubContext Hub => Pushes;

    public Ulid PluginId { get; init; } = PluginIdentity.Id;

    /// <summary>
    /// A path, not a folder: nothing here creates it, because the thing most
    /// worth proving about <c>Initialize</c> is that it touches no disk.
    /// </summary>
    public string DataFolderPath { get; init; } =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    /// <summary>The settings blob, as JSON, so a test can ask what reached it.</summary>
    /// <remarks>
    /// Settable, so a second context can be given the first one's: a plugin
    /// built over configuration that already exists is what a restart is, and
    /// a cadence is read at startup and nowhere else.
    /// </remarks>
    public FakeConfiguration Config { get; init; } = new();

    public IPluginConfiguration Configuration => Config;

    /// <summary>Protected storage. Nothing a test asserts on may ever be here and on a page.</summary>
    public FakeSecretStore Secrets { get; } = new();

    IPluginSecretStore IPluginContext.Secrets => Secrets;

    public IEventBus EventBus => throw NotProvided(nameof(EventBus));
    public IServiceProvider Services => throw NotProvided(nameof(Services));
    public HttpClient HttpClient => throw NotProvided(nameof(HttpClient));
    /// <summary>The server's library, when a test provides one.</summary>
    public FakeLibraryQuery? Shelves { get; init; }

    public IPluginLibraryQuery Library => Shelves ?? throw NotProvided(nameof(Library));
    public IPluginLibraryWriter? LibraryWriter => null;
    /// <summary>
    /// What the server has agreed to, when a test provides it.
    /// </summary>
    /// <remarks>
    /// Null by default, like everything else the plugin has not needed: a test
    /// that reaches for grants without meaning to gets a failure naming them
    /// rather than a quiet yes it would then be judged against.
    /// </remarks>
    public FakeGrants? Permits { get; init; }

    public IPluginGrants Grants => Permits ?? throw NotProvided(nameof(Grants));

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        throw NotProvided(nameof(PublishAsync));
    }

    private static NotSupportedException NotProvided(string member)
    {
        return new NotSupportedException(
            $"FakePluginContext does not provide {member}. Give it one in the test that needs it.");
    }
}
