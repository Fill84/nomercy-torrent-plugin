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
/// <strong>The folder is not asked for.</strong> The reflecting one had to
/// choose which folder of a library a show lives in, because it was building
/// the job itself. The contract takes the library and the media id, and a
/// server holding the episode row knows better than this plugin where that
/// show's files are — so <c>existing</c> is not used here, and that is the
/// point rather than an omission.
/// </para>
/// </remarks>
public sealed class ContractEncodeGateway(
    IPluginEncoder encoder,
    ILibrary library,
    IActivityJournal journal,
    ILogger logger) : IEncodeGateway
{
    public async Task<EncodeAsk> DispatchAsync(
        string stagedFile,
        EpisodeKey episode,
        Show show,
        string? existing,
        CancellationToken ct)
    {
        string name = Path.GetFileName(stagedFile);

        try
        {
            Episode? row = (await library.GetEpisodesAsync(show.Id, ct).ConfigureAwait(false))
                .FirstOrDefault(one => one.Season == episode.Season && one.Number == episode.Number);

            if (row is null)
            {
                return Refused(name, $"the server lists no {episode} for {show.Title}");
            }

            // Nought is what the field reads as on a server too old to set it,
            // because the contract added it as a member so that plugins built
            // against the older shape still construct. Asked for with no id at
            // all, the server falls back to a text search on whatever a parser
            // reads out of the file name — the encode registers against no row,
            // the queue counter moves, the library stays empty, and from
            // outside it looks like an encode still running. That is the guess
            // #35 removed, so it is refused out loud instead of made quietly.
            if (row.ServerId == 0)
            {
                return Refused(name, $"the server named no id for {episode}, and an encode asked for without one is registered against nothing");
            }

            PluginEncodeResult answer = await encoder
                .EncodeAsync(
                    stagedFile,
                    show.LibraryId,
                    row.ServerId.ToString(CultureInfo.InvariantCulture),

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

    private EncodeAsk Refused(string name, string reason)
    {
        logger.LogWarning("No encode was dispatched for {File}: {Reason}.", name, reason);
        journal.Failed(ActivityStage.Download, name, reason);

        return EncodeAsk.No;
    }
}
