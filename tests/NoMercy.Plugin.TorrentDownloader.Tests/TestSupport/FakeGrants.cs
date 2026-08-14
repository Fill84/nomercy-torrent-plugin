using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>What the owner has permitted, as far as a test is concerned.</summary>
public sealed class FakeGrants : IPluginGrants
{
    private readonly HashSet<string> _granted = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request made, in order.</summary>
    public List<(string Kind, string Host, string Reason)> Requested { get; } = [];

    /// <summary>The owner has already said yes to this host.</summary>
    public void Grant(string host)
    {
        _granted.Add(host);
    }

    public Task<bool> HasAsync(string kind, string scope, CancellationToken ct = default)
    {
        return Task.FromResult(_granted.Contains(scope));
    }

    public Task<IReadOnlyList<string>> GetAsync(string kind, CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([.. _granted]);
    }

    public Task RequestAsync(string kind, string scope, string reason, CancellationToken ct = default)
    {
        Requested.Add((kind, scope, reason));
        return Task.CompletedTask;
    }
}
