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

    // Counts every call to either GetConfiguration overload. Initialize must leave this
    // at zero: the plugin has nowhere to await, so a config read there would either block
    // or be silently skipped, and either is worse than deferring it to the first tick.
    public int Reads { get; private set; }

    // Opt-in fault for both read overloads: the real IPluginConfiguration is whole-object
    // JSON on disk, so a server killed mid-write or a full disk makes GetConfiguration(Async)
    // throw JsonException instead of returning null. Nothing exercised that path before this
    // field existed, which is the fake-fidelity gap that let a throwing read reach the host's
    // job registration unguarded.
    public Exception? ThrowOnRead { get; set; }

    // Opt-in gate for GetConfigurationAsync: lets a test hold a read open until it has
    // observed the caller's state (e.g. started a tick, then disposed the plugin) before
    // releasing it, and lets the caller's cancellation token cancel the wait instead of
    // only being checked before or after the read.
    public TaskCompletionSource<bool>? ReadGate { get; set; }

    public T? GetConfiguration<T>()
        where T : class, new()
    {
        Reads++;
        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return Stored as T;
    }

    public async Task<T?> GetConfigurationAsync<T>(CancellationToken ct = default)
        where T : class, new()
    {
        Reads++;

        if (ReadGate is not null)
        {
            await using CancellationTokenRegistration registration = ct.Register(() => ReadGate.TrySetCanceled(ct));
            await ReadGate.Task;
        }

        if (ThrowOnRead is not null)
        {
            throw ThrowOnRead;
        }

        return Stored as T;
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
