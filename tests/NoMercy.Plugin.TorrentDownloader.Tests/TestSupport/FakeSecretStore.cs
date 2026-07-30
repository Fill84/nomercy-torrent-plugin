// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IPluginSecretStore over a dictionary. The real implementation
// namespaces keys by plugin id; this fake does not, because that namespacing is not the
// gateway's concern to test - the gateway only needs to know its own keys round-trip.
public sealed class FakeSecretStore : IPluginSecretStore
{
    public Dictionary<string, string> Values { get; } = [];

    public Task<string?> GetAsync(string key, CancellationToken ct = default)
    {
        return Task.FromResult(Values.TryGetValue(key, out string? value) ? value : null);
    }

    public Task SetAsync(string key, string value, CancellationToken ct = default)
    {
        Values[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        Values.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<string>> KeysAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<string>>([.. Values.Keys]);
    }
}
