using NoMercy.Api.Plugins;
using NoMercy.Events.Plugins;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The server serving this plugin's buttons from the copy of it that is running.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Every button stopped answering after an update through the catalogue.</strong> On 17 September 2026
/// the owner updated to 0.6.2 and then 0.6.3 without a restart, and Cancel, Pause, Resume, Run and Save did
/// nothing. The server attaches a plugin's controllers when it is loaded and not again: its registrar returns at
/// once for a plugin id it has attached before. So the controllers of the copy loaded when the server started
/// went on answering, found a plugin of a type from another load context, and refused.
/// </para>
/// <para>
/// The media server is not this repository's to change, so the plugin puts it right as it is loaded: when the
/// server is serving another assembly's controllers for this plugin's id, they are detached and the running
/// copy's attached. The registrar here stands in for the server's own, by the name and the members the plugin
/// looks for.
/// </para>
/// </remarks>
public sealed class OwnEndpointsTests
{
    [Fact]
    public void AfterAnUpdateTheRunningCopysControllersAreServed()
    {
        using TorrentDownloaderPlugin running = new();
        LoadedPlugins manager = new(running) { Installed = [Info()] };

        // What the server holds after an update: the id attached to the assembly of the copy it started with.
        PluginApplicationPartRegistrar registrar = new();
        registrar.Attached[PluginIdentity.Id] = typeof(object).Assembly;

        bool attached = new OwnEndpoints(Services(manager, registrar), new CapturingLogger()).ServeCurrent(PluginIdentity.Id);

        Assert.True(attached);
        Assert.Same(typeof(TorrentDownloaderPlugin).Assembly, registrar.Attached[PluginIdentity.Id]);
    }

    /// <remarks>
    /// And nothing is touched when the server already serves the running copy, which is every ordinary start:
    /// detaching and attaching again rebuilds the server's whole route table for nothing.
    /// </remarks>
    [Fact]
    public void WhenTheRunningCopyIsServedAlreadyNothingIsTouched()
    {
        using TorrentDownloaderPlugin running = new();
        LoadedPlugins manager = new(running) { Installed = [Info()] };

        PluginApplicationPartRegistrar registrar = new();
        registrar.Attached[PluginIdentity.Id] = typeof(TorrentDownloaderPlugin).Assembly;

        bool attached = new OwnEndpoints(Services(manager, registrar), new CapturingLogger()).ServeCurrent(PluginIdentity.Id);

        Assert.False(attached);
        Assert.Equal(0, registrar.Detached);
    }

    /// <remarks>
    /// Done by the plugin itself, the moment the server says it has loaded: that is the one moment an update
    /// can be told apart, and there is nobody else to do it.
    /// </remarks>
    [Fact]
    public async Task WhenTheServerSaysThePluginHasLoadedTheRunningCopysEndpointsAreServed()
    {
        string folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", "endpoints-" + Guid.NewGuid().ToString("n")[..8]);
        using TorrentDownloaderPlugin running = new();
        LoadedPlugins manager = new(running) { Installed = [Info()] };

        PluginApplicationPartRegistrar registrar = new();
        registrar.Attached[PluginIdentity.Id] = typeof(object).Assembly;

        FakePluginContext context = new() { DataFolderPath = folder, Container = Services(manager, registrar) };

        try
        {
            running.Initialize(context);

            await context.Bus.PublishAsync(new PluginLoadedEvent
            {
                PluginId = PluginIdentity.IdText,
                PluginName = PluginIdentity.Name,
                Version = "0.0.0",
            });

            DateTimeOffset giveUpAt = DateTimeOffset.UtcNow + Hang.Limit;

            while (!ReferenceEquals(registrar.Attached[PluginIdentity.Id], typeof(TorrentDownloaderPlugin).Assembly)
                   && DateTimeOffset.UtcNow < giveUpAt)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20));
            }

            Assert.Same(typeof(TorrentDownloaderPlugin).Assembly, registrar.Attached[PluginIdentity.Id]);
        }
        finally
        {
            TemporaryFolder.Forget(folder);
        }
    }

    private static PluginInfo Info()
    {
        return new()
        {
            Id = PluginIdentity.Id,
            Name = "Torrent Downloader",
            Description = "Downloads every episode missing from a TV or anime library.",
            Version = PluginIdentity.Version,
            Status = PluginStatus.Active,
        };
    }

    private static IServiceProvider Services(IPluginManager manager, PluginApplicationPartRegistrar registrar)
    {
        return new Answering(new Dictionary<Type, object>
        {
            [typeof(IPluginManager)] = manager,
            [typeof(PluginApplicationPartRegistrar)] = registrar,
        });
    }

    private sealed class Answering(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return services.GetValueOrDefault(serviceType);
        }
    }
}
