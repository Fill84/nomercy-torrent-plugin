// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

// A settable double for IPluginManager over a single slot. The controller only ever calls
// GetPluginInstance(pluginId) - the one member real host code uses to hand a REST controller
// the live plugin it is running - so every other member here is unused by this plugin's own
// tests and throws rather than pretending to a behaviour nothing exercises.
public sealed class FakePluginManager : IPluginManager
{
    public IPlugin? Instance { get; set; }

    public Ulid InstanceId { get; set; }

    public IPlugin? GetPluginInstance(Ulid pluginId) => pluginId == InstanceId ? Instance : null;

    public IReadOnlyList<PluginInfo> GetInstalledPlugins() => throw new NotSupportedException();

    public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default) =>
        throw new NotSupportedException();

    public IEnumerable<T> GetPluginsOfType<T>()
        where T : IPlugin => throw new NotSupportedException();
}
