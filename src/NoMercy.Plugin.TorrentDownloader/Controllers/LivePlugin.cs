using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Controllers;

/// <summary>
/// The running plugin, or a sentence saying why it cannot be reached.
/// </summary>
/// <remarks>
/// <para>
/// <strong>An empty 404 is three different answers wearing one hat.</strong>
/// Every endpoint here answered <c>NotFound()</c> with no body when it could not
/// reach the plugin, and a 404 with no body is exactly what a route that does
/// not exist looks like. On 1 September 2026 the owner's <em>Run now</em> button
/// answered 404 and it took the best part of a day to work out which of the
/// three it was, because from outside they are identical:
/// </para>
/// <list type="number">
/// <item>the route was never registered, which is the server's business;</item>
/// <item>the plugin is not loaded at all;</item>
/// <item>the plugin is loaded, and it is a different type to the runtime than
/// the one this endpoint was compiled against.</item>
/// </list>
/// <para>
/// The third is the one nobody guesses. A plugin updated while the server ran
/// is loaded beside the old copy rather than over it, and a type from one load
/// context is not the same type as the identically named one from another — so
/// <c>as</c> answers null against an instance that is sitting right there.
/// </para>
/// <para>
/// <strong>And the third is put right by the controller that meets it.</strong>
/// The server attaches a plugin's controllers once, when it first loads it, and
/// goes on serving that copy's after an update (media-server #60). Until
/// contract 12 the plugin re-attached its own the moment the server said it had
/// loaded; a plugin on 12 hears no such event and holds no container. A
/// controller does — it is built by the server's own container, on a request —
/// and the stale controller is exactly the one an update leaves answering. So
/// it has the server serve the running copy's controllers, and answers this one
/// press with "press again": the route table is rebuilt behind it, and the next
/// press reaches the copy that is running.
/// </para>
/// </remarks>
internal static class LivePlugin
{
    /// <summary>The plugin this request is for, or null with the reason.</summary>
    public static TorrentDownloaderPlugin? Of(IPluginManager plugins, Ulid id, out string refusal)
    {
        return Of(plugins, services: null, id, out refusal);
    }

    /// <summary>
    /// The same, from a request whose container can put a stale controller
    /// right.
    /// </summary>
    /// <param name="plugins">The server's plugin manager, which holds the running instance.</param>
    /// <param name="services">
    /// The request's own services — the server's container — or null where the
    /// caller has none, in which case a stale controller is only named.
    /// </param>
    /// <param name="id">This plugin's id.</param>
    /// <param name="refusal">Why the plugin could not be reached, or empty when it was.</param>
    public static TorrentDownloaderPlugin? Of(IPluginManager plugins, IServiceProvider? services, Ulid id, out string refusal)
    {
        IPlugin? loaded = plugins.GetPluginInstance(id);

        if (loaded is TorrentDownloaderPlugin plugin)
        {
            refusal = string.Empty;

            return plugin;
        }

        if (loaded is null)
        {
            refusal = $"The server has no running instance of {id}, so this plugin is installed and not loaded.";

            return null;
        }

        if (services is not null && Reattached(services, id))
        {
            refusal = "The plugin was updated while the server ran, and this button belonged to the copy it "
                      + "replaced. The running copy's buttons have just been attached: press it again.";

            return null;
        }

        refusal = $"The server holds a {loaded.GetType().FullName} for {id} and this endpoint was built "
                  + $"against {typeof(TorrentDownloaderPlugin).FullName}. They are the same class from two "
                  + "load contexts, which is what an update loaded beside the old copy leaves behind. "
                  + "Restart the server and it will be one again.";

        return null;
    }

    /// <summary>
    /// Has the server serve the running copy's controllers, or finds it already
    /// does, and never takes the request down doing it.
    /// </summary>
    /// <remarks>
    /// Already serving counts: a controller reads the plugin twice for one press
    /// — once to reach it, once for the reason it could not — and the second read
    /// finds the work of the first done. Both answers have to say "press again".
    /// </remarks>
    private static bool Reattached(IServiceProvider services, Ulid id)
    {
        ILogger logger = (services.GetService(typeof(ILoggerFactory)) as ILoggerFactory)
            ?.CreateLogger(typeof(TorrentDownloaderPlugin).Namespace ?? nameof(NoMercy))
            ?? NullLogger.Instance;

        try
        {
            OwnEndpoints endpoints = new(services, logger);

            return endpoints.ServeCurrent(id) || endpoints.IsServing(id);
        }
        catch (Exception wrong)
        {
            logger.LogDebug("The plugin's endpoints were not checked: {Reason}", wrong.Message);

            return false;
        }
    }
}
