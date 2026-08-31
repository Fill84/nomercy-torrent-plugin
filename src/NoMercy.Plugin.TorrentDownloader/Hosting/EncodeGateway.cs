using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// How this server can be asked for an encode.
/// </summary>
/// <remarks>
/// <para>
/// One line of composition, which is what <see cref="IEncodeGateway"/> was made
/// a port for. There is one implementation now and it calls
/// <see cref="IPluginEncoder"/>: no server type is named that does not come from
/// <c>NoMercy.Plugins.Abstractions</c>, and there is no reflection anywhere in
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
    public static IEncodeGateway For(IServiceProvider services, IActivityJournal journal, ILogger logger)
    {
        if (services.GetService(typeof(IPluginEncoder)) is IPluginEncoder encoder)
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
            "This server does not offer IPluginEncoder, so no encode can be asked for. "
            + "The plugin needs a server carrying plugin contract 0.1.479 or newer.");

        return new NoEncoder(journal, logger);
    }

    /// <summary>Whether this server will say what became of a job, and how to ask.</summary>
    /// <remarks>
    /// Null where it will not, and the plugin then waits an encode out instead
    /// of asking about it. media-server #31.
    /// </remarks>
    public static IEncodeJobs? JobsOf(IServiceProvider services)
    {
        return services.GetService(typeof(IPluginJobs)) is IPluginJobs jobs ? new HostEncodeJobs(jobs) : null;
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
        public Task<EncodeAsk> IdentifyAsync(string stagedFile, Library library, CancellationToken ct)
        {
            return Refuse(Path.GetFileName(stagedFile));
        }

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
                "this server does not offer IPluginEncoder, so no encode can be asked for; "
                + "it needs plugin contract 0.1.479 or newer";

            logger.LogWarning("No encode was dispatched for {File}: {Reason}.", name, Reason);
            journal.Failed(ActivityStage.Download, name, Reason);

            return Task.FromResult(EncodeAsk.No);
        }
    }
}
