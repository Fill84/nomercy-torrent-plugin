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
/// <strong>And the third is the server's to put right, so the answer says
/// restart.</strong> The server attaches a plugin's controllers once, when it
/// first loads it, and goes on serving that copy's after an update
/// (media-server #60). Until 22 September 2026 the controller that met this took
/// the server's own registrar out of the request's container by name and
/// re-attached the running copy's controllers itself. That is a route into the
/// server outside the SDK, which the owner can neither see nor revoke, and
/// FiLL/nomercy-torrent-plugin#1 asks a plugin not to take one; so it is gone,
/// and a restart is what makes the buttons answer again until the server
/// attaches an updated copy's controllers on its own.
/// </para>
/// </remarks>
internal static class LivePlugin
{
    /// <summary>The plugin this request is for, or null with the reason.</summary>
    /// <param name="plugins">The server's plugin manager, which holds the running instance.</param>
    /// <param name="id">This plugin's id.</param>
    /// <param name="refusal">Why the plugin could not be reached, or empty when it was.</param>
    public static TorrentDownloaderPlugin? Of(IPluginManager plugins, Ulid id, out string refusal)
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

        refusal = $"The server holds a {loaded.GetType().FullName} for {id} and this endpoint was built "
                  + $"against {typeof(TorrentDownloaderPlugin).FullName}. They are the same class from two "
                  + "load contexts, which is what an update loaded beside the old copy leaves behind. "
                  + "Restart the server and it will be one again.";

        return null;
    }
}
