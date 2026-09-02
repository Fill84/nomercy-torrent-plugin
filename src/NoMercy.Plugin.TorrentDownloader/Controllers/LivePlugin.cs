using NoMercy.Plugins.Abstractions;

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
/// <c>as</c> answers null against an instance that is sitting right there. A
/// restart settles it, and until now nothing said so.
/// </para>
/// <para>
/// So the reason travels with the refusal. The status code stays what it was;
/// what changes is that the answer says which of the three happened.
/// </para>
/// </remarks>
internal static class LivePlugin
{
    /// <summary>The plugin this request is for, or null with the reason.</summary>
    public static TorrentDownloaderPlugin? Of(IPluginManager plugins, Ulid id, out string refusal)
    {
        IPlugin? loaded = plugins.GetPluginInstance(id);

        if (loaded is TorrentDownloaderPlugin plugin)
        {
            refusal = string.Empty;

            return plugin;
        }

        refusal = loaded is null
            ? $"The server has no running instance of {id}, so this plugin is installed and not loaded."
            : $"The server holds a {loaded.GetType().FullName} for {id} and this endpoint was built "
              + $"against {typeof(TorrentDownloaderPlugin).FullName}. They are the same class from two "
              + "load contexts, which is what an update loaded beside the old copy leaves behind. "
              + "Restart the server and it will be one again.";

        return null;
    }
}
