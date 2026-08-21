using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The host's plugin manager, holding one plugin.
/// </summary>
/// <remarks>
/// A controller reaches the live plugin through <see cref="IPluginManager"/>,
/// because the host builds it from the server's own container and nothing this
/// plugin defines is in there. This stands in for that container in a test, and
/// it answers for any id: a controller built outside a request has no route to
/// read one from, so <c>PluginId</c> is <see cref="Ulid.Empty"/>.
/// </remarks>
public sealed class LoadedPlugins(IPlugin plugin) : IPluginManager
{
    /// <remarks>
    /// Answers for its own plugin and for nothing else. Handing the plugin back
    /// whatever was asked for would pass a controller that looked it up under
    /// the wrong id, which is the one thing this stands in to prove.
    /// </remarks>
    public IPlugin? GetPluginInstance(Ulid pluginId)
    {
        return pluginId == plugin.Id ? plugin : null;
    }

    public IEnumerable<T> GetPluginsOfType<T>()
        where T : IPlugin
    {
        return plugin is T only ? [only] : [];
    }

    public IReadOnlyList<PluginInfo> GetInstalledPlugins()
    {
        return [];
    }

    public Task InstallPluginAsync(string packageUrl, CancellationToken ct = default)
    {
        throw new NotSupportedException("A test does not install plugins.");
    }

    public Task InstallPluginAsync(
        string packageUrl,
        string? expectedChecksum,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("A test does not install plugins.");
    }

    public Task InstallPluginArchiveAsync(
        string archivePath,
        string? expectedChecksum = null,
        CancellationToken ct = default)
    {
        throw new NotSupportedException("A test does not install plugins.");
    }

    public Task EnablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task DisablePluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task UninstallPluginAsync(Ulid pluginId, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PluginLoadResult>> LoadAllAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginLoadResult>>([]);
    }
}
