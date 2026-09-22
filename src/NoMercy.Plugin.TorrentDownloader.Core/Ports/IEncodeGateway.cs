using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>What came of asking for an encode.</summary>
/// <remarks>
/// <para>
/// <strong>The job id is back, and for a reason that is the reverse of the one
/// it left for.</strong> It went on 14 September 2026 because nothing read it:
/// the plugin heard the server's own encoding events, those carry the media row
/// the encode registers against, and the id handed back here — a hash of the
/// job's payload, chosen because a queue row id is not stable — matched none of
/// them. On contract 12 a plugin has no bus to hear those events on. What it has
/// is <c>IPluginJobs.StatusAsync</c>, which answers for exactly this id and
/// nothing else, so this is now the only handle on what became of the encode.
/// </para>
/// <para>
/// Written down with the grab (<c>encode_jobs</c>), because the case worth
/// answering is the one memory cannot: the plugin restarts, the grab is still
/// dispatched, and nothing knows whether the job it asked for is running or was
/// thrown away with the queue.
/// </para>
/// </remarks>
/// <param name="Taken">Whether the server took it. False leaves the file staged.</param>
/// <param name="JobId">
/// What the server called the job it queued, or null where it took the ask
/// without naming one. Asked about by <c>IEncoderSays</c> on every pass after.
/// </param>
public sealed record EncodeAsk(bool Taken, string? JobId = null)
{
    /// <summary>Refused, with the reason already said out loud by whoever refused it.</summary>
    public static EncodeAsk No { get; } = new(false);
}

/// <summary>
/// How this plugin asks the media server to encode a staged episode into the
/// owner's library.
/// </summary>
/// <remarks>
/// <para>
/// This is the last thing the plugin does and the only thing it exists for: it
/// stages a finished video and asks the server to take it. Everything else the
/// plugin asks of the server already sits behind a port here — the library, the
/// torrent client, the name pool, the source ledger, the journal — and for a
/// while this one did not, so the cadence that keeps the owner's library
/// filling named a concrete host class.
/// </para>
/// <para>
/// <strong>It has one method, and that is the whole of what the plugin may ask
/// for.</strong> There was a second — hand a file over with no id and let the
/// server work out what it is — and the server's own source says it does
/// nothing: the id goes into <c>VideoEncodeJob.Id</c>, which resolves against
/// <c>Movies.Id</c> or <c>Episodes.Id</c> and nothing else, so with no id the
/// job returns having done no work while the queue records it finished. An
/// episode this plugin cannot name is an episode it does not ask for.
/// </para>
/// <para>
/// <strong>The port earned itself.</strong> The day the contract landed, the
/// reflecting implementation was replaced by a class beside it and one line
/// where the plugin is composed, with no line of <c>Transfers</c> touched. It
/// is not a seam invented for a test.
/// </para>
/// </remarks>
public interface IEncodeGateway
{
    /// <summary>
    /// Asks for one staged file to be encoded into the show's own library, and
    /// says whether the ask was taken.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>It never throws.</strong> An encode that cannot be asked for is
    /// one download left staged and the next tick asking again — it used to
    /// throw out of a reflection call and unwind the whole transfers pass,
    /// so one type mismatch stopped every download in flight from being looked
    /// at.
    /// </para>
    /// <para>
    /// <strong>A refusal says why, here, before it returns false.</strong> The
    /// caller learns only that it was not taken, and it acts the same way
    /// whatever the reason: leave the file staged and ask again next tick.
    /// So an implementation that returns false without putting the reason in
    /// the log and the journal leaves the owner with an episode that never
    /// arrives and nothing anywhere saying why — which is exactly what three of
    /// theirs did.
    /// </para>
    /// </remarks>
    /// <param name="stagedFile">The video, waiting in the intake folder.</param>
    /// <param name="episode">
    /// Which episode it is, as the library answered: the numbers, and the
    /// server's own id for the row. The plugin chose the show, the season and
    /// the number, so it knows this and the server does not have to work it out
    /// from the file's name — which is what it did while every episode the
    /// owner staged on 24 August 2026 went nowhere.
    ///
    /// The row rather than the key, so the id comes from the answer the tick
    /// already has. Looked up in here it was one question per episode, and a
    /// season pack asked the same one nine times.
    /// </param>
    /// <param name="show">
    /// The show it belongs to, and with it the library the episode goes back
    /// to. An anime episode lands in the anime library and a television one in
    /// the tv library, because the server decided the media type when the show
    /// was filed and this plugin follows it rather than choosing.
    /// </param>
    /// <param name="ct">Cancellation.</param>
    Task<EncodeAsk> DispatchAsync(
        string stagedFile,
        Episode episode,
        Show show,
        CancellationToken ct);
}
