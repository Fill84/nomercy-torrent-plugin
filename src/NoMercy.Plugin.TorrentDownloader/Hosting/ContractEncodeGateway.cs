using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Asks the server for an encode through the plugin contract, by calling a
/// method.
/// </summary>
/// <remarks>
/// <para>
/// The only implementation of <see cref="IEncodeGateway"/>. What it replaced —
/// <c>EncodeDispatch</c>, 588 lines of it — reached into the server by name,
/// because there was no other way to ask: <c>IJobDispatcher</c> to queue with,
/// <c>VideoEncodeJob</c> to queue, <c>MediaContext</c> to find the episode row
/// in. It broke four times on server changes it could not see coming.
/// </para>
/// <para>
/// This plugin opened two media-server issues about that and both closed on
/// 30 August 2026: #30 gives plugins <see cref="IPluginEncoder"/>, so an encode
/// is asked for rather than assembled; #35 puts the server's own episode id in
/// the library answer, so the row is handed over rather than looked up. There
/// is no reflection in this file, no server type named in it that does not come
/// from the contract — and, since the old one was deleted, none anywhere in the
/// plugin.
/// </para>
/// <para>
/// <strong>No folder is chosen here.</strong> The reflecting one had to pick
/// which folder of a library a show lives in, because it was building the job
/// itself, and it asked the server for the show's files on every dispatch to do
/// it. The contract takes the library and the media id: a server holding the
/// episode row knows where that show's files are, and the question is not asked
/// any more.
/// </para>
/// </remarks>
public sealed class ContractEncodeGateway(
    IPluginEncoder encoder,
    IActivityJournal journal,
    ILogger logger) : IEncodeGateway
{
    public async Task<EncodeAsk> DispatchAsync(
        string stagedFile,
        Episode episode,
        Show show,
        CancellationToken ct)
    {
        string name = Path.GetFileName(stagedFile);

        try
        {
            // Nought is what the field reads as on a server too old to set it,
            // because the contract added it as a member so that plugins built
            // against the older shape still construct. Asked for with no id at
            // all, the server falls back to a text search on whatever a parser
            // reads out of the file name — the encode registers against no row,
            // the queue counter moves, the library stays empty, and from
            // outside it looks like an encode still running. That is the guess
            // #35 removed, so it is refused out loud instead of made quietly.
            if (episode.ServerId == 0)
            {
                return Refused(name, $"the server named no id for {episode.Key}, and an encode asked for without one is registered against nothing");
            }

            PluginEncodeResult answer = await encoder
                .EncodeAsync(
                    stagedFile,
                    show.LibraryId,
                    episode.ServerId.ToString(CultureInfo.InvariantCulture),

                    // Null keeps the library's own presets, which is not this
                    // plugin's decision to make.
                    presetId: null,
                    ct)
                .ConfigureAwait(false);

            if (!answer.Accepted)
            {
                return Refused(name, answer.Refusal ?? "the server refused it and said nothing about why");
            }

            journal.Finished(ActivityStage.Download, name, $"encode dispatched to library {show.LibraryId}");

            // With the job it queued, which is what makes a failed encode
            // something the plugin can be told about rather than something it
            // waits six hours to infer.
            return new(true, answer.JobId);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            // It never throws. An encode that cannot be asked for is one
            // download left staged and the next tick asking again; throwing
            // out of here would unwind the whole transfers cadence, so one
            // failure would stop every download in flight from being looked at.
            return Refused(name, wrong.Message);
        }
    }

    public async Task<EncodeAsk> IdentifyAsync(string stagedFile, Library library, CancellationToken ct)
    {
        string name = Path.GetFileName(stagedFile);

        try
        {
            PluginEncodeResult answer = await encoder
                .EncodeAsync(
                    stagedFile,
                    library.Id,

                    // No id, which tells the server to identify the file from
                    // its name. Everywhere else in this plugin that is the
                    // fault to avoid; here it is the only thing there is, and
                    // it is what Add content does with a file a person points
                    // at.
                    mediaId: null,
                    presetId: null,
                    ct)
                .ConfigureAwait(false);

            if (!answer.Accepted)
            {
                return Refused(name, answer.Refusal ?? "the server refused it and said nothing about why");
            }

            journal.Finished(
                ActivityStage.Download,
                name,
                $"handed to {library.Name} for the server to identify");

            return new(true, answer.JobId);
        }
        catch (Exception wrong) when (wrong is not OperationCanceledException)
        {
            return Refused(name, wrong.Message);
        }
    }

    private EncodeAsk Refused(string name, string reason)
    {
        logger.LogWarning("No encode was dispatched for {File}: {Reason}.", name, reason);
        journal.Failed(ActivityStage.Download, name, reason);

        return EncodeAsk.No;
    }
}
