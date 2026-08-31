using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

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
/// <strong>It is the one part already scheduled to change.</strong>
/// media-server #30 gives plugins <c>IPluginEncoder</c>, so an encode can be
/// asked for without reaching into <c>IJobDispatcher</c> and
/// <c>VideoEncodeJob</c> by name; #35 puts the episode's id in the contract, so
/// the row no longer has to be read out of <c>MediaContext</c>. Between them
/// every line of reflection in the plugin goes.
/// </para>
/// <para>
/// <strong>What that day looks like.</strong> A second implementation of this
/// interface beside the first, and one line where the plugin is composed. The
/// reflecting one stays until the owner is on a server that has the contract,
/// and then it is deleted whole — no line of <c>Transfers</c> is touched either
/// way. That is what this interface is for; it is not a seam invented for a
/// test.
/// </para>
/// </remarks>
/// <summary>What came of asking for an encode.</summary>
/// <param name="Taken">Whether the server took it. False leaves the file staged.</param>
/// <param name="JobId">
/// The job it queued, where the server named one. Null both when it was refused
/// and when it was taken by a server with no way to name the job — the older
/// dispatch cannot, because it builds the job itself and hands it to a queue
/// that answers nothing.
///
/// It is what <see cref="IEncodeJobs"/> is asked about, so a grab that has one
/// can be told a dead job from a slow one instead of waiting six hours to find
/// out which it was.
/// </param>
public sealed record EncodeAsk(bool Taken, string? JobId)
{
    /// <summary>Refused, with the reason already said out loud by whoever refused it.</summary>
    public static EncodeAsk No { get; } = new(false, null);
}

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
    /// throw out of a reflection call and unwind the whole transfers cadence,
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
