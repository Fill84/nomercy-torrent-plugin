using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Plugins;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
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
    /// <para>
    /// <strong>Done by the controller an update left behind, on the first press.</strong> Until contract 12 the
    /// plugin did this the moment the server said it had loaded; a plugin on 12 hears no such event and holds no
    /// container. A controller does — the server's own container builds it, on a request — and the stale
    /// controller is exactly the one the update leaves answering: it finds the running plugin to be a stranger, has
    /// the server serve the running copy's controllers, and answers "press it again".
    /// </para>
    /// <para>
    /// The stranger here is what the server holds after an update: an instance of another type under this
    /// plugin's id, in the assembly the server will attach.
    /// </para>
    /// </remarks>
    [Fact]
    public void AStaleControllerHasTheRunningCopysEndpointsServedAndAsksForAnotherPress()
    {
        Stranger updated = new();
        LoadedPlugins manager = new(updated) { Installed = [Info()] };

        PluginApplicationPartRegistrar registrar = new();
        registrar.Attached[PluginIdentity.Id] = typeof(object).Assembly;

        SettingsController stale = new SettingsController(manager).On(PluginIdentity.Id);
        stale.HttpContext.RequestServices = Services(manager, registrar);

        NotFoundObjectResult refused = Assert.IsType<NotFoundObjectResult>(stale.Advanced());

        Assert.Contains("press it again", refused.Value?.ToString() ?? string.Empty, StringComparison.Ordinal);
        Assert.Same(typeof(Stranger).Assembly, registrar.Attached[PluginIdentity.Id]);
        Assert.Equal(1, registrar.Detached);
    }

    /// <summary>The plugin as the server holds it after an update: this id, another type.</summary>
    private sealed class Stranger : IPlugin
    {
        public Ulid Id => PluginIdentity.Id;

        public string Name => PluginIdentity.Name;

        public string Description => PluginIdentity.Description;

        public Version Version => PluginIdentity.Version;

        public void Initialize(IPluginContext context)
        {
        }

        public void Dispose()
        {
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
