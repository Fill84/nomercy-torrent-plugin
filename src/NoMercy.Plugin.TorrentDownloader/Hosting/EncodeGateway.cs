using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Which way this server can be asked for an encode.
/// </summary>
/// <remarks>
/// <para>
/// One line of composition, which is what <see cref="IEncodeGateway"/> was made
/// a port for. A server that offers <see cref="IPluginEncoder"/> is asked
/// through the contract and nothing is reflected at all; a server that does not
/// is asked the way it always was.
/// </para>
/// <para>
/// <strong>The old way stays until nobody is running an old server.</strong>
/// This plugin is installed on servers the owner does not control the upgrade
/// of, and an encode that stops working is a library that stops filling. When
/// <see cref="EncodeDispatch"/> goes it goes whole — it is the only reflection
/// in the plugin — and that is a decision about who is running what rather than
/// a technical one.
/// </para>
/// </remarks>
public static class EncodeGateway
{
    /// <summary>Picks the way this server can be asked, and says which it was.</summary>
    public static IEncodeGateway For(
        IServiceProvider services,
        ILibrary library,
        IActivityJournal journal,
        ILogger logger)
    {
        if (services.GetService(typeof(IPluginEncoder)) is IPluginEncoder encoder)
        {
            logger.LogInformation(
                "The server offers IPluginEncoder, so encodes are asked for through the contract.");

            return new ContractEncodeGateway(encoder, library, journal, logger);
        }

        // Said once, at the level the owner reads, because it is the difference
        // between an episode named by the server's own id and one the server
        // has to identify again from a file name.
        logger.LogInformation(
            "This server does not offer IPluginEncoder, so encodes are dispatched the older way.");

        return new EncodeDispatch(services, journal, logger);
    }
}
