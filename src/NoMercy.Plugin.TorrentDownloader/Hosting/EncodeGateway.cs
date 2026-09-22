using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// How this server can be asked for an encode.
/// </summary>
/// <remarks>
/// <para>
/// One line of composition, which is what <see cref="IEncodeGateway"/> was made
/// a port for. There is one implementation now and it calls
/// <see cref="IPluginEncoder"/>: no server type is named that does not come from
/// <c>NoMercy.PluginSdk.Abstractions</c>, and there is no reflection anywhere in
/// this plugin.
/// </para>
/// <para>
/// <strong>What went with it.</strong> <c>EncodeDispatch</c> was 588 lines that
/// reached into the server by name — <c>IJobDispatcher</c>, <c>VideoEncodeJob</c>,
/// <c>MediaContext</c> — because there was no other way to ask. It broke four
/// times on server changes it could not see coming, which is why media-server
/// #30 and #35 were opened, and it is deleted whole now that they are closed.
/// </para>
/// <para>
/// A server too old to offer the contract is told so, once, in words the owner
/// can act on. Guessing at the old way instead is what this plugin no longer
/// does.
/// </para>
/// </remarks>
public static class EncodeGateway
{
    /// <summary>The gateway for this server, or one that says why there is none.</summary>
    /// <param name="encoder">
    /// <c>IPluginContext.Encoder</c>: the facade the server hands a plugin whose
    /// manifest names the encoder hook, and null for one whose manifest does
    /// not — or on a server that mediates no encoder at all. The context is
    /// the only place a plugin on contract 12 gets it; there is no container to
    /// ask.
    /// </param>
    /// <param name="journal">Where a refusal is said for the owner to read.</param>
    /// <param name="logger">Where which gateway was chosen is said, once.</param>
    public static IEncodeGateway For(IPluginEncoder? encoder, IActivityJournal journal, ILogger logger)
    {
        if (encoder is not null)
        {
            // Said out loud, because which of the two was chosen decides
            // whether anything this plugin downloads is ever encoded, and a
            // log that only speaks up when it goes wrong leaves the owner
            // guessing on the run where it went right.
            logger.LogInformation(
                "This server offers IPluginEncoder ({Encoder}), so encodes are asked for over the contract.",
                encoder.GetType().Name);

            return new ContractEncodeGateway(encoder, journal, logger);
        }

        // Said at the level the owner reads, because nothing else they can see
        // says it: downloads would go on finishing and staging, and every one
        // of them would sit in the intake folder waiting for an encode that
        // could never be asked for.
        logger.LogWarning(
            "This server handed the plugin no IPluginEncoder, so no encode can be asked for. "
            + "The server hands it over only to a plugin whose manifest names the encoder hook, "
            + "and only once the owner has approved that on the plugin's page.");

        return new NoEncoder(journal, logger);
    }

    /// <summary>
    /// The gateway for a server that cannot be asked at all.
    /// </summary>
    /// <remarks>
    /// It refuses and says why, which is what every implementation of the port
    /// owes its caller: the caller learns only "not taken" and leaves the file
    /// staged, so a refusal that says nothing is an episode that never arrives
    /// with nothing anywhere to explain it.
    /// </remarks>
    private sealed class NoEncoder(IActivityJournal journal, ILogger logger) : IEncodeGateway
    {
        public Task<EncodeAsk> DispatchAsync(
            string stagedFile,
            Episode episode,
            Show show,
            CancellationToken ct)
        {
            return Refuse(Path.GetFileName(stagedFile));
        }

        /// <summary>The one thing it can do, said the same way every time.</summary>
        private Task<EncodeAsk> Refuse(string name)
        {
            const string Reason =
                "this server handed the plugin no IPluginEncoder, so no encode can be asked for; "
                + "the plugin's manifest names the encoder hook, and the owner approves it on the plugin's page";

            logger.LogWarning("No encode was dispatched for {File}: {Reason}.", name, Reason);
            journal.Failed(ActivityStage.Download, name, Reason);

            return Task.FromResult(EncodeAsk.No);
        }
    }
}
