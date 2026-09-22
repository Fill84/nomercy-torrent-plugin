using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The host's context, with a fake behind each part a test may need and a
/// refusal that names the part behind every other.
/// </summary>
/// <remarks>
/// <para>
/// Only what <see cref="IPluginContext"/> makes a host answer is here. The
/// facades the contract defaults — the encoder, the jobs, the server's own
/// information — are null or refused exactly as a server that wires none
/// leaves them, so a test that needs one says so by giving it.
/// </para>
/// <para>
/// There is no container and no bus, because contract 12 gives a plugin
/// neither: what a plugin used to reach through them it reaches through the
/// facades here or not at all.
/// </para>
/// </remarks>
public sealed class FakePluginContext : IPluginContext
{
    public CapturingLogger Log { get; } = new();

    public ILogger Logger => Log;

    public FakeHub Pushes { get; } = new();

    public IPluginHubContext Hub => Pushes;

    public Ulid PluginId { get; init; } = PluginIdentity.Id;

    public string DataFolderPath { get; init; } =
        Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

    public FakeConfiguration Config { get; init; } = new();

    public IPluginConfiguration Configuration => Config;

    public FakeSecretStore Secrets { get; } = new();

    IPluginSecretStore IPluginContext.Secrets => Secrets;

    public FakeEvents Messages { get; } = new();

    public IPluginEvents Events => Messages;

    public HttpClient HttpClient => throw NotProvided(nameof(HttpClient));

    public FakeLibraryQuery? Shelves { get; init; }

    public IPluginLibraryQuery Library => Shelves ?? throw NotProvided(nameof(Library));

    public IPluginLibraryWriter? LibraryWriter => null;

    public FakeGrants? Permits { get; init; }

    public IPluginGrants Grants => Permits ?? throw NotProvided(nameof(Grants));

    /// <summary>The encoder the server hands a plugin whose manifest names the hook, or none.</summary>
    public FakeEncoder? Encodes { get; init; }

    public IPluginEncoder? Encoder => Encodes;

    /// <summary>What the server says became of a job, or none.</summary>
    public FakeJobs? Jobs { get; init; }

    IPluginJobs? IPluginContext.Jobs => Jobs;

    /// <summary>Where the server says it can write, or a facade that refuses as an unwired one does.</summary>
    public IReadOnlyList<PluginStorageLocation>? GrantedPaths { get; init; }

    public IPluginServerInfo Server => GrantedPaths is IReadOnlyList<PluginStorageLocation> granted
        ? new ServerInfo(granted)
        : throw new PluginRefusedException(PluginRefusalMessages.FacadeNotOnThisHost(PluginId.ToString(), "IPluginContext.Server"));

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        return Messages.PublishAsync(name, payload, ct);
    }

    private static NotSupportedException NotProvided(string member)
    {
        return new NotSupportedException(
            $"FakePluginContext does not provide {member}. Give it one in the test that needs it.");
    }

    private sealed class ServerInfo(IReadOnlyList<PluginStorageLocation> granted) : IPluginServerInfo
    {
        public Version Version => new(0, 0);

        public string Platform => "test";

        public IReadOnlyList<PluginStorageLocation> GrantedPaths => granted;

        public Task<long> FreeSpaceBytesAsync(string folderId, CancellationToken ct = default)
        {
            return Task.FromResult(0L);
        }
    }
}

/// <summary>Plugin-to-plugin messages, kept rather than delivered: nothing in this plugin subscribes.</summary>
public sealed class FakeEvents : IPluginEvents
{
    public List<(string Name, object? Payload)> Published { get; } = [];

    public void Subscribe<T>(string topic, Func<T, CancellationToken, Task> handler)
    {
        throw new NotSupportedException("This plugin subscribes to no plugin message.");
    }

    public Task PublishAsync<T>(string name, T payload, CancellationToken ct = default)
    {
        Published.Add((name, payload));

        return Task.CompletedTask;
    }
}
