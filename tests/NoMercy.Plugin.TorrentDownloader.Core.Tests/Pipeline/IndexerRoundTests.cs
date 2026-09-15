using Microsoft.Extensions.Time.Testing;

using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.IndexerSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// One wish group's release names put to every indexer, against the pages the indexers really answered.
/// </summary>
/// <remarks><c>docs/specs/indexer-search.md</c> § Asking the indexers and <c>run.md</c> § The order of a run.</remarks>
public sealed class IndexerRoundTests
{
    /// <remarks>
    /// For a show TorrentBay is asked and then LimeTorrents, one after the other, and only then every other
    /// enabled indexer at the same time. The rest are held on the clock: all six are waiting together before
    /// any of them may answer, which cannot happen unless they were started together, and not one of them
    /// was asked before LimeTorrents had its question.
    /// </remarks>
    [Fact]
    public async Task FirstChoiceIndexersAreAskedOneAfterAnotherThenTheRestTogether()
    {
        FakeTimeProvider clock = new();
        FakeFetch fetch = new FakeFetch(clock).AnsweringSilo(restTakes: TimeSpan.FromSeconds(5));

        Task<IReadOnlyList<RankedTorrent>> asking = Round(fetch).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        await Until(() => fetch.InFlight == 6);

        string[] asked = [.. fetch.Asked.Select(address => address.ToString())];

        int torrentBay = Array.IndexOf(asked, Exact(TorrentBay, Silo));
        int limeTorrents = Array.IndexOf(asked, Exact(LimeTorrents, Silo));

        Assert.Equal(0, torrentBay);
        Assert.True(limeTorrents > torrentBay, "LimeTorrents was not asked after TorrentBay.");

        foreach (SourceDefinition rest in (SourceDefinition[])[ThePirateBay, X1337, Eztv, TorrentGalaxy, Torrentz2, TorrentDownloads])
        {
            Assert.True(Array.IndexOf(asked, Exact(rest, Silo)) > limeTorrents, $"{rest.Name} was asked before the first-choice indexers were done.");
        }

        clock.Advance(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(await asking);
    }

    /// <remarks>For an anime Nyaa is asked first, then TorrentBay, then LimeTorrents.</remarks>
    [Fact]
    public async Task ForAnAnimeNyaaIsAskedFirstThenTorrentBayThenLimeTorrents()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSoloLeveling();

        await Round(fetch, [LimeTorrents, TorrentBay, Nyaa]).AskAsync([SoloLeveling], SoloLevelingEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Equal(
            ["nyaa.si", "extranet.torrentbay.st", "www.limetorrents.lol"],
            fetch.Asked.Select(address => address.Host).Distinct());
    }

    /// <remarks>
    /// Each name is asked letter for letter first, and an indexer that has nothing for the exact name is
    /// asked the same name without its punctuation. TorrentGalaxy answered the exact name with nothing and
    /// the words with the release; LimeTorrents answered the exact name, so it is not asked a second time.
    /// </remarks>
    [Fact]
    public async Task EachNameIsAskedExactlyThenWithoutPunctuation()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        string[] asked = [.. fetch.Asked.Select(address => address.ToString())];

        Assert.Contains(Words(TorrentGalaxy, Silo), asked);
        Assert.True(Array.IndexOf(asked, Exact(TorrentGalaxy, Silo)) < Array.IndexOf(asked, Words(TorrentGalaxy, Silo)));
        Assert.Contains(Exact(LimeTorrents, Silo), asked);
        Assert.DoesNotContain(Words(LimeTorrents, Silo), asked);

        Assert.Contains(ranked, torrent => torrent.Indexers.Contains("TorrentGalaxy"));
    }

    /// <remarks>
    /// A row counts only when its title is the release name: the same letters and digits in the same order,
    /// with case, punctuation and the site's own tag set aside — <c>[TGx]</c>, a trailing <c>EZTV</c>,
    /// <c>[EZTVx.to].mkv</c>. TorrentGalaxy's answer to the words also carried S02E10 and S02E11 of the
    /// same group: those are not the name, so their pages are never read and they are part of no torrent.
    /// </remarks>
    [Fact]
    public async Task ARowCountsOnlyWhenItsTitleIsTheName()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();

        IReadOnlyList<RankedTorrent> ranked = await Round(fetch).AskAsync([Silo], SiloEpisode, Blacklist.None, new(), CancellationToken.None);

        Assert.Contains("Torrentz2", Assert.Single(ranked, torrent => torrent.Torrent.InfoHash == EztvHash).Indexers);
        Assert.Contains("TorrentGalaxy", Assert.Single(ranked, torrent => torrent.Torrent.InfoHash == TgxHash).Indexers);

        Assert.DoesNotContain(fetch.Asked, address => address.ToString().Contains("s02e10", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(fetch.Asked, address => address.ToString().Contains("s02e11", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Capture.Rows("round-silo-torrentgalaxy-words.html", "torrentgalaxy"), title => title.StartsWith("Silo S02E10", StringComparison.Ordinal));
    }

    /// <remarks>
    /// A question already asked of an indexer during a run is not asked of it again in that run — nor a
    /// row's page read again that was read for the same question.
    /// </remarks>
    [Fact]
    public async Task AQuestionAlreadyAskedThisRunIsNotAskedAgain()
    {
        FakeFetch fetch = new FakeFetch().AnsweringSilo();
        IndexerRound round = Round(fetch);
        AskedThisCycle asked = new();

        IReadOnlyList<RankedTorrent> first = await round.AskAsync([Silo], SiloEpisode, Blacklist.None, asked, CancellationToken.None);
        int requests = fetch.Asked.Count;

        IReadOnlyList<RankedTorrent> second = await round.AskAsync([Silo], SiloEpisode, Blacklist.None, asked, CancellationToken.None);

        Assert.NotEmpty(first);
        Assert.Equal(requests, fetch.Asked.Count);
        Assert.Equal(first.Select(torrent => torrent.Torrent.InfoHash), second.Select(torrent => torrent.Torrent.InfoHash));
    }

    private static async Task Until(Func<bool> condition)
    {
        using CancellationTokenSource bounded = new(TimeSpan.FromSeconds(10));

        while (!condition())
        {
            bounded.Token.ThrowIfCancellationRequested();
            await Task.Delay(10, CancellationToken.None);
        }
    }
}
