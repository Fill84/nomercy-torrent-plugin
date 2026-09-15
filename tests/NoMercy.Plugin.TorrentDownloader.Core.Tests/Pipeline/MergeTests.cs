using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.IndexerSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>Rows of one info hash, from any number of indexers, as one torrent.</summary>
/// <remarks><c>docs/specs/indexer-search.md</c> § Merging by hash.</remarks>
public sealed class MergeTests
{
    /// <remarks>
    /// The TGx upload is six rows on six indexers — two carrying the hash on the listing, four read off the
    /// row's own page — and it is one torrent. Its magnet carries every tracker any of those rows gave, and
    /// the EZTV upload of the same name stays a torrent of its own.
    /// </remarks>
    [Fact]
    public async Task RowsOfOneHashFromAnyIndexersAreOneTorrentWithEveryTracker()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        RankedTorrent tgx = Assert.Single(ranked, torrent => torrent.Torrent.InfoHash == TgxHash);

        Assert.Equal(
            ["1337x", "LimeTorrents", "The Pirate Bay", "TorrentDownloads", "TorrentGalaxy", "Torrentz2"],
            tgx.Indexers.Order(StringComparer.Ordinal));

        foreach (string page in (string[])["round-silo-1337x-detail-tgx.html", "round-silo-torrentdownloads-detail-tgx.html", "round-silo-torrentgalaxy-detail-tgx.html"])
        {
            string magnet = DetailPage.Read(Capture.Fixture(page), Silo)!.Value.Magnet;

            Assert.All(Magnets.TrackersOf(magnet), tracker => Assert.Contains(tracker, tgx.Torrent.Trackers));
        }

        Assert.NotEmpty(tgx.Torrent.Trackers);
        Assert.Single(ranked, torrent => torrent.Torrent.InfoHash == EztvHash);
    }
}
