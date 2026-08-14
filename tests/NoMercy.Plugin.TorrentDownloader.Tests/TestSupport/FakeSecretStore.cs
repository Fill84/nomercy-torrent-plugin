using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>Protected storage, as far as a test is concerned.</summary>
public sealed class FakeSecretStore : IPluginSecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(_secrets.GetValueOrDefault(key));
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        _secrets[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        _secrets.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([.. _secrets.Keys]);
    }
}
