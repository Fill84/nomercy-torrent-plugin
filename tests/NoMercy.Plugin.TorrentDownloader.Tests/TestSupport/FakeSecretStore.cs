using NoMercy.PluginSdk.Abstractions;

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

    // Per-user secrets, which contract 12 added and this plugin keeps none of:
    // a passkey and an API key belong to the server's owner, not to whoever is
    // looking at the page.
    public Task<string?> GetForUserAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult<string?>(null);
    }

    public Task SetForUserAsync(string key, string value, CancellationToken ct = default)
    {
        throw new NotSupportedException("This plugin keeps no per-user secret.");
    }

    public Task DeleteForUserAsync(string key, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }
}
