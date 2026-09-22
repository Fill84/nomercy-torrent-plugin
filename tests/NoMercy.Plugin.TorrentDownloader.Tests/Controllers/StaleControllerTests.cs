using Microsoft.AspNetCore.Mvc;
using NoMercy.Api.Plugins;
using NoMercy.Plugin.TorrentDownloader.Controllers;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.PluginSdk.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Controllers;

/// <summary>
/// A controller an update left behind says a restart puts it right, and leaves the server's route table alone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The plugin no longer rewires the server.</strong> Until 22 September 2026 the stale controller took the
/// server's own <c>PluginApplicationPartRegistrar</c> out of the request's container by name and detached and
/// attached controllers itself, so the first press after an update could ask for a second. That is the route into
/// the server FiLL/nomercy-torrent-plugin#1 asks a plugin not to take: a type outside the SDK, reached through the
/// server's container, which the owner can neither see nor revoke. The fix belongs to the server
/// (media-server #60); until it lands, a restart is what puts the buttons right, and the answer says so.
/// </para>
/// <para>
/// The registrar here stands in for the server's, holding the old copy's assembly as the server does after an
/// update, and is handed to the controller exactly as the server's container would hand it over.
/// </para>
/// </remarks>
public sealed class StaleControllerTests
{
    [Fact]
    public void AStaleControllerAsksForARestartAndLeavesTheServersRoutesAlone()
    {
        Stranger updated = new();
        LoadedPlugins manager = new(updated) { Installed = [Info()] };

        PluginApplicationPartRegistrar registrar = new();
        registrar.Attached[PluginIdentity.Id] = typeof(object).Assembly;

        SettingsController stale = new SettingsController(manager).On(PluginIdentity.Id);
        stale.HttpContext.RequestServices = new Answering(new Dictionary<Type, object>
        {
            [typeof(IPluginManager)] = manager,
            [typeof(PluginApplicationPartRegistrar)] = registrar,
        });

        NotFoundObjectResult refused = Assert.IsType<NotFoundObjectResult>(stale.Advanced());
        string reason = refused.Value?.ToString() ?? string.Empty;

        Assert.Contains("Restart the server", reason, StringComparison.Ordinal);
        Assert.DoesNotContain("press it again", reason, StringComparison.Ordinal);
        Assert.Same(typeof(object).Assembly, registrar.Attached[PluginIdentity.Id]);
        Assert.Equal(0, registrar.Detached);
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

    private sealed class Answering(Dictionary<Type, object> services) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            return services.GetValueOrDefault(serviceType);
        }
    }
}
