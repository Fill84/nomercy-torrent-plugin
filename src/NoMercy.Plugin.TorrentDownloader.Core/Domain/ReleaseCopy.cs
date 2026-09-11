namespace NoMercy.Plugin.TorrentDownloader.Core.Domain;

/// <summary>
/// One indexer's way to the torrent, kept through a merge.
/// </summary>
/// <remarks>
/// <para>
/// An indexer exists to hand over a torrent or a magnet, and every tracker
/// comes off that artefact rather than off the listing it was found on — not
/// one shipped indexer publishes a magnet on a listing, which is measured
/// across every capture in <c>tests/fixtures/</c>. So the same torrent on five
/// sites is five artefacts to be had, and the trackers of all five belong on
/// the one magnet that is handed to the client.
/// </para>
/// <para>
/// Merging used to keep only the best-informed row and drop the rest, and with
/// them went every other site's route to the same torrent. What reached the
/// client was a magnet built from the hash alone, with no tracker in it at all,
/// and the swarm could then only be found through the DHT.
/// </para>
/// </remarks>
/// <param name="Source">Which indexer this route belongs to, so each is asked once.</param>
/// <param name="DetailUrl">The row's own page, where the magnet usually is.</param>
/// <param name="Magnet">The magnet, where the listing itself carried one.</param>
/// <param name="Claim">What the site must be asked, where it publishes no address at all.</param>
public sealed record CopyRoute(
    string Source,
    Uri? DetailUrl = null,
    string? Magnet = null,
    Sources.Readers.SignedClaim? Claim = null);

/// <summary>
/// One copy of one release, on one site.
/// </summary>
/// <remarks>
/// A copy is what a name becomes once an indexer has answered for it. The
/// difference matters more than it sounds: a name has no seeders, no size and
/// no site, and 0.3.4 asked one how many seeders it had, got nought, and
/// refused every announcement it ever saw.
/// </remarks>
/// <param name="Title">The release name this copy is of, as the site printed it.</param>
/// <param name="Source">Which site answered with it, for the history line and the ranking.</param>
/// <param name="Priority">That site's rating. Higher is better.</param>
/// <param name="InfoHash">What copies of one release are merged by, when the site gives it.</param>
/// <param name="Magnet">The magnet, when the site publishes one.</param>
/// <param name="DetailUrl">The row's own page, which is the usual route to a torrent.</param>
/// <param name="Seeders">
/// How many are serving it, or null when the site does not say. Null is not
/// nought: judging a copy on a number nobody gave is the same category error as
/// judging a name on one.
/// </param>
/// <param name="SizeBytes">How big it is, or null when the site does not say.</param>
/// <param name="Trackers">
/// Every tracker any site's magnet named for this torrent. The same torrent on
/// five sites is one torrent with five sets of trackers, and more trackers is a
/// faster download — which is the whole reason every indexer is asked rather
/// than the first one that answers.
/// </param>
public sealed record ReleaseCopy(
    string Title,
    string Source,
    int Priority,
    string? InfoHash = null,
    string? Magnet = null,
    Uri? DetailUrl = null,
    int? Seeders = null,
    long? SizeBytes = null,
    IReadOnlyList<string>? Trackers = null)
{
    /// <summary>
    /// What its site must be asked before it will name the torrent, when the
    /// row carries neither a magnet nor a hash.
    /// </summary>
    /// <remarks>
    /// It travels with the copy because the tokens it holds belong to the page
    /// the row was read from, and a token from another page is refused.
    /// </remarks>
    public Sources.Readers.SignedClaim? Claim { get; init; }

    /// <summary>Never null: a copy naming no tracker names none.</summary>
    public IReadOnlyList<string> Trackers { get; init; } = Trackers ?? [];

    /// <summary>
    /// Every indexer's way to this torrent, one per indexer, kept through the
    /// merge so all of them can be asked for the artefact their trackers are on.
    /// </summary>
    public IReadOnlyList<CopyRoute> Routes { get; init; } = [];
}
