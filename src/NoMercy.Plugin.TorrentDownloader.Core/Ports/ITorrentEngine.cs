namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// Where a torrent stands.
/// </summary>
/// <remarks>
/// <c>FetchingMetadata</c> and <c>Stalled</c> are states of their own and not
/// shades of downloading. A magnet has no file list until its metadata arrives,
/// and reporting that as "nought per cent downloading" makes a torrent that
/// will never resolve look like one about to start.
/// </remarks>
public enum TorrentState
{
    /// <summary>A magnet with no metadata yet: no name, no files, no size.</summary>
    FetchingMetadata,

    /// <summary>Verifying what is already on disk.</summary>
    Checking,

    Downloading,

    Seeding,

    /// <summary>
    /// Every wanted byte is here and the client has stopped.
    /// </summary>
    /// <remarks>
    /// Not <see cref="Paused"/>, which is the owner having stopped it, and not
    /// <see cref="Seeding"/>, which is still giving something back. A public
    /// torrent reaches this the moment it completes, because nothing is ever
    /// uploaded on a public swarm. What it is waiting for is staging, and once
    /// that has happened the grab is done and the row leaves the page.
    /// </remarks>
    Finished,

    /// <summary>No progress <em>and</em> no peers. Progress without peers is not this.</summary>
    Stalled,

    Paused,

    /// <summary>
    /// Waiting for a free slot, because <c>MaxConcurrentDownloads</c> are
    /// already running.
    /// </summary>
    /// <remarks>
    /// Its own state and not <see cref="Paused"/>: one was stopped by the owner
    /// and the other is about to start on its own, and an owner can act on the
    /// difference. Sixteen torrents dialling at once is how none of them
    /// finished.
    /// </remarks>
    Queued,

    Stopped,

    Error,
}

/// <summary>
/// A torrent the plugin wants downloaded.
/// </summary>
/// <param name="Source">A magnet URI, or the address of a <c>.torrent</c>.</param>
/// <param name="Trackers">
/// Every tracker known for it — the union of what each site's magnet named and
/// the owner's own list. More trackers is a faster download.
/// </param>
/// <param name="DownloadFolder">Where the bytes land while it downloads.</param>
/// <param name="ExpectedBytes">
/// What the indexer said it weighs, when it said. Null is not nought: a site
/// that published no size has not said the file is empty.
/// </param>
public sealed record TorrentRequest(
    string Source,
    IReadOnlyList<string> Trackers,
    string DownloadFolder,
    long? ExpectedBytes);

/// <summary>What the client calls a torrent it has taken on.</summary>
/// <param name="InfoHash">Forty hex characters, upper case.</param>
/// <param name="Name">What it is called, or null while the metadata has not arrived.</param>
public sealed record TorrentHandle(string InfoHash, string? Name);

/// <summary>One file inside a torrent.</summary>
/// <param name="Path">Its path under the torrent's own folder.</param>
/// <param name="Length">How many bytes.</param>
public sealed record TorrentFile(string Path, long Length);

/// <summary>
/// What one transfer is doing, as a page would say it.
/// </summary>
/// <remarks>
/// Every number here is real or null. A count that is not known says so rather
/// than being drawn as nought — 0.3.4 showed "0 downloads" while two were
/// running, and that is the rule this whole record is shaped by.
/// </remarks>
/// <param name="InfoHash">Which torrent.</param>
/// <param name="Name">Its name, or null while the metadata has not arrived.</param>
/// <param name="State">Where it stands.</param>
/// <param name="BytesDone">How much is on disk and verified.</param>
/// <param name="BytesTotal">How big it is, or null while nothing knows.</param>
/// <param name="DownloadRateBytesPerSecond">Measured, not averaged over the whole transfer.</param>
/// <param name="UploadRateBytesPerSecond">The same, going out.</param>
/// <param name="Peers">How many are connected.</param>
/// <param name="Seeds">How many of those have all of it.</param>
/// <param name="Ratio">Uploaded over downloaded, or null before anything has been downloaded.</param>
/// <param name="Eta">How long it has left, or null when that cannot be worked out.</param>
/// <param name="Error">What went wrong, in its own words, or null.</param>
/// <param name="SwarmSeeds">
/// How many seeds the trackers say the whole swarm has, or null before one
/// answered. Not what this client is connected to: nought connected out of
/// three hundred is a client that has not met anybody yet, and nought out of
/// nought is a dead release.
/// </param>
/// <param name="SwarmPeers">The same for the peers still downloading it.</param>
/// <param name="ErrorIsTheRelease">
/// Whether <paramref name="Error"/> is a property of the release or of this
/// moment. "There is no video file in it" is true of that torrent for ever and
/// there is no point ever asking for it again; "no peer sent its metadata
/// within five minutes" is true of one evening. On 25 August 2026 that second
/// one refused South Park S15E12 1080p HMAX CtrlHD, and on 31 August the same
/// release sat on TorrentBay with fifty seeders while the plugin still would
/// not look at it — because both were blacklisted the same way, for ever. This
/// is what tells them apart.
/// </param>
public sealed record TorrentStatus(
    string InfoHash,
    string? Name,
    TorrentState State,
    long BytesDone,
    long? BytesTotal,
    double DownloadRateBytesPerSecond,
    double UploadRateBytesPerSecond,
    int Peers,
    int Seeds,
    double? Ratio,
    TimeSpan? Eta,
    string? Error,
    int? SwarmSeeds = null,
    int? SwarmPeers = null,
    bool ErrorIsTheRelease = false);

/// <summary>
/// The torrent client, as the pipeline is allowed to see it.
/// </summary>
/// <remarks>
/// The client itself is written in this repository and lives in its own
/// assembly, which references nothing. This port is how everything else reaches
/// it, so <c>Core</c> never sees a socket.
/// </remarks>
public interface ITorrentEngine
{
    /// <summary>Takes on a torrent and answers what it will be known by.</summary>
    Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct);

    /// <summary>One row per torrent it is holding.</summary>
    Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct);

    Task PauseAsync(string infoHash, CancellationToken ct);

    Task ResumeAsync(string infoHash, CancellationToken ct);

    /// <summary>Stops it and forgets it, keeping or deleting what is on disk.</summary>
    Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct);

    /// <summary>
    /// Every file in it, or nothing at all while the metadata has not arrived.
    /// </summary>
    /// <remarks>
    /// Empty rather than a guess: a magnet has no file list until its metadata
    /// is fetched, and inventing one from the name is how the wrong file gets
    /// staged.
    /// </remarks>
    Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct);
}
