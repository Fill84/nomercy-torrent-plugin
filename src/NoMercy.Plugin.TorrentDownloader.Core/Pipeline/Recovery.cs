using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>Where a grab stands, as the store records it.</summary>
public enum GrabState
{
    /// <summary>Chosen and handed to the client, nothing more known.</summary>
    Grabbed,

    Downloading,

    /// <summary>Finished, staged and dispatched.</summary>
    Done,

    Failed,

    Paused,
}

/// <summary>
/// One download the store knows about.
/// </summary>
/// <param name="InfoHash">Forty hex characters, upper case.</param>
/// <param name="Magnet">
/// What it was grabbed from. This is what a torrent is re-added from after a
/// restart, which is why it is kept rather than being thrown away once the
/// client has taken it.
/// </param>
/// <param name="ReleaseTitle">What it is called, for the journal.</param>
/// <param name="State">Where the store thinks it is.</param>
public sealed record StoredDownload(string InfoHash, string Magnet, string ReleaseTitle, GrabState State)
{
    /// <summary>Every episode this grab answers for.</summary>
    /// <remarks>
    /// One for an ordinary release, several for a season pack. It is what
    /// staging matches files against and what a failure puts back to missing,
    /// and neither can be worked out from anything else the store holds.
    /// </remarks>
    public IReadOnlyList<EpisodeKey> Covers { get; init; } = [];
}

/// <summary>
/// What has to happen to make the client and the store agree again.
/// </summary>
/// <param name="Add">In the store and not in the client: re-add, resume intact.</param>
/// <param name="Stop">
/// In the client and not in the store: stop it and keep the files. Something
/// the plugin has no record of is not something to delete — the files may be
/// half a film the owner has been waiting for, and a record can be lost by a
/// restore of an older database.
/// </param>
/// <param name="Stage">
/// Finished while nothing was watching. <strong>F4</strong>: 0.3.4 only ever
/// noticed a completion while it was running, so a download that finished
/// during a restart sat there for ever and the episode was never dispatched.
/// </param>
/// <param name="Carry">In both, and running. Nothing to do.</param>
public sealed record RecoveryPlan(
    IReadOnlyList<StoredDownload> Add,
    IReadOnlyList<TorrentStatus> Stop,
    IReadOnlyList<StoredDownload> Stage,
    IReadOnlyList<StoredDownload> Carry);

/// <summary>
/// Making the client and the store agree, on the way up and on every tick.
/// </summary>
/// <remarks>
/// <para>
/// The two drift apart for ordinary reasons: the server was killed mid-write,
/// a torrent finished while it was down, a database was restored from
/// yesterday. docs/06-torrent-client.md § Recovery gives one answer for each
/// case and this is that table, as a function of its arguments so that every
/// case can be put to it.
/// </para>
/// <para>
/// It decides and does nothing. Adding, stopping and staging are the caller's,
/// which keeps this testable without a client, a disk or a network.
/// </para>
/// </remarks>
public static class Recovery
{
    /// <summary>Works out what has to happen.</summary>
    /// <param name="stored">What the plugin's own records say.</param>
    /// <param name="running">What the client says it is holding.</param>
    public static RecoveryPlan Plan(
        IReadOnlyList<StoredDownload> stored,
        IReadOnlyList<TorrentStatus> running)
    {
        Dictionary<string, TorrentStatus> byHash = new(StringComparer.OrdinalIgnoreCase);

        foreach (TorrentStatus status in running)
        {
            byHash[status.InfoHash] = status;
        }

        List<StoredDownload> add = [];
        List<StoredDownload> stage = [];
        List<StoredDownload> carry = [];

        foreach (StoredDownload download in stored)
        {
            if (download.State is GrabState.Done or GrabState.Failed)
            {
                // Finished with, either way. A torrent still seeding after it
                // was staged is in the client on purpose and is not something
                // to stop.
                continue;
            }

            if (!byHash.TryGetValue(download.InfoHash, out TorrentStatus? status))
            {
                // The client has never heard of it. Its bytes are still on
                // disk and its resume file with them, so re-adding costs a
                // verification pass and not a re-download.
                add.Add(download);

                continue;
            }

            if (Finished(status))
            {
                stage.Add(download);
            }
            else
            {
                carry.Add(download);
            }
        }

        HashSet<string> known = new(stored.Select(one => one.InfoHash), StringComparer.OrdinalIgnoreCase);

        return new(
            add,
            [.. running.Where(one => !known.Contains(one.InfoHash))],
            stage,
            carry);
    }

    /// <summary>
    /// Whether the client says this one is done.
    /// </summary>
    /// <remarks>
    /// Every byte on disk, not the state alone: a seeding torrent and a stopped
    /// one are both finished, and a torrent stopped halfway is neither. A size
    /// nobody knows yet — a magnet still fetching its metadata — is not
    /// finished however little has been downloaded, which is the trap in
    /// comparing two numbers that can both be nought.
    /// </remarks>
    public static bool Finished(TorrentStatus status)
    {
        return status.BytesTotal is > 0 && status.BytesDone >= status.BytesTotal;
    }
}
