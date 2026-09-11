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

    /// <remarks>
    /// <para>
    /// <strong>The owner's rule of 11 September 2026, and the reason the sources
    /// exist at all.</strong> A source hands over the exact release, and that
    /// exact release is what an indexer is asked for first — letter for letter,
    /// dots and dash included. No rows, and the same name is asked without its
    /// punctuation. No rows to that either, and only then does the site go
    /// down the ladder.
    /// </para>
    /// <para>
    /// Every name used to go out as loose words — the dots and the dash turned
    /// into spaces before any site saw it — so the question the source had
    /// answered was never put to anybody.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASourcesNameIsAskedLetterForLetterThenWithoutPunctuationAndOnlyThenTheLadder()
    {
        FakeFetch fetch = new();

        // LimeTorrents answers the name exactly as the source wrote it.
        fetch.Answers(Exact("LimeTorrents", Full), Capture.Fixture("limetorrents.html"));

        // TorrentDownloads answers neither form of the name, and does answer
        // the first rung of the ladder.
        fetch.Answers(Exact("TorrentDownloads", Full), Capture.Fixture("nyaa-nothing.xml"));
        fetch.Answers(Address("TorrentDownloads", Full), Capture.Fixture("nyaa-nothing.xml"));
        fetch.Answers(Address("TorrentDownloads", Shorter), Capture.Fixture("torrentdownloads.html"));

        await Finding(fetch).SearchAsync(
            SearchTerm.Ladder([Full], [Shorter, Shortest]),
            LibraryKind.Television,
            CancellationToken.None);

        // Letter for letter, then without punctuation, then the ladder — and it
        // stopped on the rung that answered.
        Assert.Equal(
            [Exact("TorrentDownloads", Full), Address("TorrentDownloads", Full), Address("TorrentDownloads", Shorter)],
            fetch.Asked.Where(address => address.Host == "www.torrentdownloads.pro").Select(address => address.ToString()));

        // The site that answered the exact name was asked nothing else.
        Assert.Equal(
            [Exact("LimeTorrents", Full)],
            fetch.Asked.Where(address => address.Host == "www.limetorrents.lol").Select(address => address.ToString()));

        // And letter for letter means the dots and the dash reached the site.
        Assert.Contains(Full, Uri.UnescapeDataString(Exact("LimeTorrents", Full)), StringComparison.Ordinal);
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

    /// <summary>The address a name goes out on letter for letter, only made safe for a URL.</summary>
    private static string Exact(string site, string term)
    {
        string written = site == "LimeTorrents"
            ? "https://www.limetorrents.lol/search/all/{query}/"
            : "https://www.torrentdownloads.pro/search/?search={query}";

        return new Uri(written.Replace("{query}", Uri.EscapeDataString(term), StringComparison.Ordinal)).ToString();
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
