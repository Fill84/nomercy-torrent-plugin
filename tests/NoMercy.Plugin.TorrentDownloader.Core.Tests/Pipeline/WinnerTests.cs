using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.IndexerSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>Which merged torrent wins.</summary>
/// <remarks><c>docs/specs/indexer-search.md</c> § The winner.</remarks>
public sealed class WinnerTests
{
    /// <remarks>
    /// The TGx upload is on five indexers and the EZTV upload on two. The one on the most indexers wins, and
    /// the rest follow in the same order.
    /// </remarks>
    [Fact]
    public async Task TheTorrentOnTheMostIndexersWins()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Equal([TgxHash, EztvHash], ranked.Select(torrent => torrent.Torrent.InfoHash));
        Assert.Equal([5, 2], ranked.Select(torrent => torrent.Indexers.Count));

        // And more indexers wins over found first: a torrent found first on one indexer loses to one found
        // later on two.
        RankedTorrent foundFirstOnOne = Torrent(TgxHash, "LimeTorrents", foundAt: 1000);
        RankedTorrent foundLaterOnTwo = new(new ReleaseCopy(Silo, "The Pirate Bay", 0, EztvHash), ["The Pirate Bay", "TorrentGalaxy"], 2001);

        Assert.Equal(EztvHash, Winner.Order([foundFirstOnOne, foundLaterOnTwo], Blacklist.None)[0].Torrent.InfoHash);
    }

    /// <remarks>
    /// Level on indexers, the one found on a first-choice indexer wins: first-choice indexers are asked
    /// first, so what they found was found first. Handed over in the other order, it still wins.
    /// </remarks>
    [Fact]
    public void ATieGoesToAFirstChoiceIndexer()
    {
        RankedTorrent onTorrentGalaxy = Torrent(EztvHash, "TorrentGalaxy", foundAt: 6000);
        RankedTorrent onLimeTorrents = Torrent(TgxHash, "LimeTorrents", foundAt: 1000);

        Assert.Equal(TgxHash, Winner.Order([onTorrentGalaxy, onLimeTorrents], Blacklist.None)[0].Torrent.InfoHash);
    }

    /// <remarks>For an anime, level on indexers, the one found on Nyaa wins over one found only on TorrentBay or LimeTorrents.</remarks>
    [Fact]
    public void ForAnAnimeATieGoesToNyaa()
    {
        RankedTorrent onTorrentBay = Torrent(TgxHash, "TorrentBay", foundAt: 1000);
        RankedTorrent onNyaa = Torrent(EztvHash, "Nyaa", foundAt: 0);

        Assert.Equal(EztvHash, Winner.Order([onTorrentBay, onNyaa], Blacklist.None)[0].Torrent.InfoHash);
    }

    /// <remarks>
    /// Still level, the one found first wins: both uploads on one indexer alone, in the order its page listed
    /// them. The Pirate Bay lists the TGx upload first and TorrentGalaxy lists the EZTV upload first, so the
    /// winner follows the page and not the hash. Handed over the other way round, the order holds.
    /// </remarks>
    [Theory]
    [InlineData("The Pirate Bay", TgxHash, EztvHash)]
    [InlineData("TorrentGalaxy", EztvHash, TgxHash)]
    public async Task StillLevelTheOneFoundFirstWins(string indexer, string first, string second)
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();
        SourceDefinition alone = Shipped.Single(one => one.Name == indexer);

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch, [alone]).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Equal([first, second], ranked.Select(torrent => torrent.Torrent.InfoHash));
        Assert.Equal([1, 1], ranked.Select(torrent => torrent.Indexers.Count));
        Assert.Equal(
            [first, second],
            Winner.Order([.. ranked.Reverse()], Blacklist.None).Select(torrent => torrent.Torrent.InfoHash));
    }

    /// <remarks>
    /// A release or hash the download client failed, and that is still refused, takes no part: the TGx hash
    /// refused leaves the EZTV upload the winner, and the release's name refused leaves nothing.
    /// </remarks>
    [Fact]
    public async Task ARefusedReleaseOrHashTakesNoPart()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();
        IndexerRound round = Round(fetch);

        IReadOnlyList<RankedTorrent> hashRefused = await round.AskAsync([Silo], SiloEpisode, Blacklist.Of(TgxHash), new(), CancellationToken.None);
        Assert.Equal(EztvHash, hashRefused[0].Torrent.InfoHash);
        Assert.DoesNotContain(hashRefused, torrent => torrent.Torrent.InfoHash == TgxHash);

        IReadOnlyList<RankedTorrent> nameRefused = await round.AskAsync([Silo], SiloEpisode, Blacklist.Of(Blacklist.KeyOf(Silo)), new(), CancellationToken.None);
        Assert.Empty(nameRefused);
    }

    /// <remarks>
    /// The torrent listed most is the TGx upload, but where no indexer carries its hash on the listing and
    /// no indexer's page for it can be read, it cannot be offered. The next torrent in the same order — the
    /// EZTV upload, readable on TorrentGalaxy — becomes the winner.
    /// </remarks>
    [Fact]
    public async Task AnUnreadableWinnerYieldsToTheNext()
    {
        FakeFetch fetch = new FakeFetch()
            .AnsweringSilo()
            .Fails(X1337Detail, FetchOutcome.Unreachable, "gone")
            .Fails(TorrentGalaxyTgxDetail, FetchOutcome.Unreachable, "gone")
            .Fails(new Uri(TorrentDownloadsDetail).ToString(), FetchOutcome.Unreachable, "gone")
            .Fails(TorrentDownloadsDetail, FetchOutcome.Unreachable, "gone");

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch, [X1337, TorrentGalaxy, TorrentDownloads, Eztv])
            .AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Equal(EztvHash, ranked[0].Torrent.InfoHash);
        Assert.DoesNotContain(ranked, torrent => torrent.Torrent.InfoHash == TgxHash);
    }

    private static RankedTorrent Torrent(string hash, string indexer, int foundAt)
    {
        return new(new ReleaseCopy(Silo, indexer, 0, hash), [indexer], foundAt);
    }
}
