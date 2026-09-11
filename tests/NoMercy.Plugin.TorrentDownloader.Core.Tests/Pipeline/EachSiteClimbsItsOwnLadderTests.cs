using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// A site that answers nothing is asked something simpler, and a site that
/// answered is left alone.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The owner's rule, 10 September 2026: no result means the question
/// was wrong for that site, so simplify it and ask again.</strong> Measured
/// against the real indexers the same day, asking every one of them for
/// <c>Silo.S03E06.1080p.WEB.H264-CAKES</c> and then for shorter forms of it:
/// </para>
/// <code>
/// indexer            as published   Silo S03E06 1080p   Silo S03E06
/// The Pirate Bay                1                   5            13
/// 1337x                         0                   4             9
/// LimeTorrents                  2                  11            22
/// TorrentGalaxy                 5                  50            50
/// </code>
/// <para>
/// 1337x has the release and answers nothing to the name it is published
/// under; one rung down it answers four rows. Under a ladder climbed by every
/// site at once it would never get that rung, because The Pirate Bay answered
/// on the first — so 1337x would contribute nothing, and the trackers it
/// publishes would never reach the magnet. That is the whole point of asking
/// every indexer in the first place.
/// </para>
/// </remarks>
public class EachSiteClimbsItsOwnLadderTests
{
    /// <remarks>
    /// The site that answered stops where it answered. Climbing on would spend
    /// a request on a question already answered and drag back the broader rows
    /// this exists to avoid.
    /// </remarks>
    [Fact]
    public async Task ASiteThatAnswersNothingIsAskedTheNextRungAndOneThatAnsweredIsNot()
    {
        FakeFetch fetch = new();

        // Nothing at all for the full release name, on either site.
        fetch.Answers(Address("LimeTorrents", Full), Capture.Fixture("nyaa-nothing.xml"));
        fetch.Answers(Address("TorrentDownloads", Full), Capture.Fixture("nyaa-nothing.xml"));

        // One rung down, LimeTorrents answers and TorrentDownloads still does
        // not.
        fetch.Answers(Address("LimeTorrents", Shorter), Capture.Fixture("limetorrents.html"));
        fetch.Answers(Address("TorrentDownloads", Shorter), Capture.Fixture("nyaa-nothing.xml"));

        // And two rungs down, TorrentDownloads finally does.
        fetch.Answers(Address("TorrentDownloads", Shortest), Capture.Fixture("torrentdownloads.html"));

        IReadOnlyList<ReleaseCopy> found = await Finding(fetch).SearchAsync(
            [Full, Shorter, Shortest],
            LibraryKind.Television,
            CancellationToken.None);

        // Both sites are in the answer, which is the whole point.
        Assert.Contains(found, copy => copy.Source == "LimeTorrents");
        Assert.Contains(found, copy => copy.Source == "TorrentDownloads");

        // LimeTorrents stopped where it answered.
        Assert.DoesNotContain(fetch.Asked, address => address.ToString() == Address("LimeTorrents", Shortest));

        // And TorrentDownloads climbed all three rungs, because it answered on
        // none of the first two.
        Assert.Contains(fetch.Asked, address => address.ToString() == Address("TorrentDownloads", Full));
        Assert.Contains(fetch.Asked, address => address.ToString() == Address("TorrentDownloads", Shorter));
        Assert.Contains(fetch.Asked, address => address.ToString() == Address("TorrentDownloads", Shortest));
    }

    /// <remarks>
    /// A rung nobody needs is asked by nobody. Every site answering on the
    /// first means the broader questions are never put at all, which is what
    /// keeps the rubbish out.
    /// </remarks>
    [Fact]
    public async Task NoBroaderQuestionIsAskedWhenEverySiteAnsweredTheFirst()
    {
        FakeFetch fetch = new();

        fetch.Answers(Address("LimeTorrents", Full), Capture.Fixture("limetorrents.html"));
        fetch.Answers(Address("TorrentDownloads", Full), Capture.Fixture("torrentdownloads.html"));

        await Finding(fetch).SearchAsync(
            [Full, Shorter, Shortest],
            LibraryKind.Television,
            CancellationToken.None);

        Assert.Equal(2, fetch.Asked.Count);
    }

    private const string Full = "Silo.S03E06.1080p.WEB.H264-CAKES";
    private const string Shorter = "Silo S03E06 1080p";
    private const string Shortest = "Silo S03E06";

    private static string Address(string site, string term)
    {
        string written = site == "LimeTorrents"
            ? $"https://www.limetorrents.lol/search/all/{term}/"
            : $"https://www.torrentdownloads.pro/search/?search={term}";

        return new Uri(Query.Write(written.Replace(term, "{query}", StringComparison.Ordinal), term, QueryStyles.Words))
            .ToString();
    }

    private static Find Finding(FakeFetch fetch)
    {
        return new(
            SourceCatalogue.Build(Sources, [], []),
            fetch,
            Readers.Shipped(),
            new ActivityJournal());
    }

    private static readonly SourceDefinition[] Sources =
    [
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/") { Priority = 35 },
        new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
        {
            Reader = "torrentdownloads",
            Priority = 25,
        },
    ];
}
