// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IPluginConfiguration over an in-memory object. Records every
// object it was asked to save so a test can serialise it and assert a secret's literal
// text never appears anywhere in it - a property assertion would keep passing while a
// newly added field leaked the value, so tests must inspect the JSON itself.
public sealed class FakeConfiguration : IPluginConfiguration
{
    public object? Stored { get; set; }

    public List<object> SavedObjects { get; } = [];

    public T? GetConfiguration<T>()
        where T : class, new()
    {
        return Stored as T;
    }

    public Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        return Task.FromResult(Stored as T);
    }

    public void SaveConfiguration<T>(T configuration)
        where T : class
    {
        Stored = configuration;
        SavedObjects.Add(configuration);
    }

    public Task SaveConfigurationAsync<T>(T configuration, CancellationToken ct = default)
        where T : class
    {
        SaveConfiguration(configuration);
        return Task.CompletedTask;
    }

    public bool HasConfiguration()
    {
        return Stored is not null;
    }

    public void DeleteConfiguration()
    {
        Stored = null;
    }
}
