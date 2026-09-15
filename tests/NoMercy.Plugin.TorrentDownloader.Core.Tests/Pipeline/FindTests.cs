using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// One question to one indexer, and a torrent read off the routes that have it.
/// </summary>
/// <remarks>
/// Every page here is one a site really sent, detail pages included: those were
/// captured through the same tool, at the address a row on the listing really
/// carried. What is asked, in what order, and which torrent wins is
/// <c>IndexerRoundTests</c>' business.
/// </remarks>
public class FindTests
{
    /// <remarks>
    /// <strong>C3.</strong> No shipped indexer publishes a magnet on its
    /// listing, so the row's own page is the only route to one — and 0.3.4
    /// wrote that address and read it nowhere, so TorrentBay produced rows for
    /// weeks and zero downloads. The page here is a real TorrentDownloads detail
    /// page, which carries no magnet at all and prints the bare hash.
    /// </remarks>
    [Fact]
    public async Task ARowWithNoMagnetIsFollowedToItsOwnPage()
    {
        FakeFetch fetch = new();
        fetch.Answers(Detail, Capture.Fixture("torrentdownloads-detail.html"));

        ReleaseCopy chosen = new(Name, "TorrentDownloads", 25, null, null, new(Detail), 9);

        ReleaseCopy followed = await Finding(fetch).ResolveAsync(chosen, CancellationToken.None);

        Assert.Equal("D8C536D10926761FCC69265308070B19DB6DA336", followed.InfoHash);
        Assert.StartsWith(
            "magnet:?xt=urn:btih:D8C536D10926761FCC69265308070B19DB6DA336",
            followed.Magnet,
            StringComparison.Ordinal);

        Assert.Single(fetch.Asked);
    }

    /// <remarks>
    /// And a detail page that does publish a magnet is read for it, trackers
    /// and all. This one is a real LimeTorrents page, at the address its own
    /// listing row carried.
    /// </remarks>
    [Fact]
    public async Task ADetailPageThatPublishesAMagnetIsReadForIt()
    {
        const string Page = "https://www.limetorrents.lol/Silo-S03E06-1080p-WEB-H264-CAKES-torrent-19877003.html";

        FakeFetch fetch = new();
        fetch.Answers(Page, Capture.Fixture("limetorrents-detail.html"));

        ReleaseCopy followed = await Finding(fetch).ResolveAsync(
            new(Name, "LimeTorrents", 35, null, null, new(Page), 12),
            CancellationToken.None);

        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", followed.InfoHash);
        Assert.NotEmpty(followed.Trackers);
    }

    /// <remarks>
    /// Once, and only for the release that was chosen. Following every row of
    /// every answer is a request per row per episode, which is the shape of
    /// thing that gets a plugin banned from a site.
    /// </remarks>
    [Fact]
    public async Task ACopyThatAlreadyHasAMagnetIsNotFollowedAtAll()
    {
        FakeFetch fetch = new();

        ReleaseCopy already = new(
            Name,
            "LimeTorrents",
            35,
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            new(Detail),
            12);

        ReleaseCopy resolved = await Finding(fetch).ResolveAsync(already, CancellationToken.None);

        // Nothing was asked, which is the whole of what this is about: the
        // artefact was already in hand.
        Assert.Empty(fetch.Asked);
        Assert.Equal(already.Magnet, resolved.Magnet);
        Assert.Equal(already.InfoHash, resolved.InfoHash);
    }

    /// <remarks>
    /// <para>
    /// <strong>A hash is a fallback, never a reason not to look.</strong> This
    /// used to stop the moment a row carried one: a magnet was built out of the
    /// hash — <c>magnet:?xt=urn:btih:…&amp;dn=…</c>, with no tracker in it — and
    /// the page was never read. Every tracker this plugin could ever learn is on
    /// that page, so nothing was learned from any indexer, ever, and every grab
    /// went out with an empty tracker list.
    /// </para>
    /// <para>
    /// The page is a real LimeTorrents one and the hash on the row is the hash
    /// on the page, so the copy loses nothing by being read — and gains the
    /// twenty trackers the site published with it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ACopyThatCarriesAHashIsStillReadForItsTrackers()
    {
        const string Page = "https://www.limetorrents.lol/Silo-S03E06-1080p-WEB-H264-CAKES-torrent-19877003.html";

        FakeFetch fetch = new();
        fetch.Answers(Page, Capture.Fixture("limetorrents-detail.html"));

        ReleaseCopy hashed = new(
            Name,
            "LimeTorrents",
            35,
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            null,
            new(Page),
            12);

        ReleaseCopy answered = await Finding(fetch).ResolveAsync(hashed, CancellationToken.None);

        Assert.Single(fetch.Asked);
        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", answered.InfoHash);
        Assert.NotEmpty(answered.Trackers);
    }

    /// <remarks>
    /// One indexer being down is one indexer being down: it answers no rows and
    /// the journal says why, and the next site is still asked.
    /// </remarks>
    [Fact]
    public async Task OneIndexerThatFailsDoesNotTakeTheSearchDown()
    {
        FakeFetch fetch = new();
        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.FailsHost("torrentz2.nz", FetchOutcome.RateLimited, "torrentz2.nz answered 429");

        ActivityJournal journal = new();

        Find find = Finding(fetch, journal: journal);

        ReleaseCopy[] failed = await find.AskAsync(Indexers[1], new(Name, false), null, CancellationToken.None);
        ReleaseCopy[] answered = await find.AskAsync(Indexers[0], new(Name, false), null, CancellationToken.None);

        Assert.Empty(failed);
        Assert.NotEmpty(answered);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Find
                     && entry.Outcome == ActivityOutcome.Failed
                     && entry.Subject.Contains("Torrentz2", StringComparison.Ordinal));

        Assert.Empty(journal.Snapshot().InFlight);
    }

    /// <remarks>
    /// The Sources page is opened after the fact, usually because an episode did
    /// not arrive, and the journal it would otherwise have to read is bounded at
    /// five hundred entries and gone by then. So every ask writes down what the
    /// site answered: how many rows, or its refusal in its own words.
    /// </remarks>
    [Fact]
    public async Task EveryAskIsWrittenDownWithWhatTheSiteAnswered()
    {
        FakeFetch fetch = new();
        RecordingLedger ledger = new();

        // LimeTorrents answers the name letter for letter. Torrentz2 refuses
        // both forms of it.
        fetch.Answers(Exact("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.Fails(Exact("Torrentz2"), FetchOutcome.RateLimited, "429 Too Many Requests");
        fetch.Fails(Address("Torrentz2"), FetchOutcome.RateLimited, "429 Too Many Requests");

        Find find = Finding(fetch, ledger: ledger);

        await find.AskAsync(Indexers[0], new(Name, true), null, CancellationToken.None);
        await find.AskAsync(Indexers[1], new(Name, true), null, CancellationToken.None);
        await find.AskAsync(Indexers[1], new(Name, false), null, CancellationToken.None);

        SourceAnswer answered = ledger.Answers.Single(one => one.Name == "LimeTorrents");

        Assert.True(answered.Rows > 0, "The captured page is covered in releases.");
        Assert.Null(answered.Refusal);

        // Both questions it was put, each written down.
        SourceAnswer[] refused = [.. ledger.Answers.Where(one => one.Name == "Torrentz2")];

        Assert.Equal(2, refused.Length);

        // Its own words. "Broken" would be this plugin's judgement of a site
        // that simply asked to be left alone for a while, which is G2 exactly.
        Assert.All(refused, one =>
        {
            Assert.Equal(0, one.Rows);
            Assert.Contains("429", one.Refusal!, StringComparison.Ordinal);
        });
    }

    /// <remarks>
    /// <para>
    /// docs/05-sources.md scopes Nyaa to <em>indexer (anime)</em>, and nothing
    /// in the catalogue could say so until now. A television search that asked
    /// it spends a request per episode on a site carrying almost no television
    /// at all — and every one of those requests is paced, so it is taken from
    /// the sources that would have answered.
    /// </para>
    /// <para>
    /// A source that names no library is for all of them. Saying nothing has to
    /// mean everywhere, or adding the field would silently switch off every
    /// source that had not been given one.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(LibraryKind.Television, false)]
    [InlineData(LibraryKind.Anime, true)]
    public void ASourceIsAskedOnlyAboutTheLibrariesItNames(LibraryKind kind, bool asked)
    {
        IReadOnlyList<SourceDefinition> indexers = Finding(new FakeFetch(), sources: [.. Indexers, AnimeOnly]).IndexersFor(kind);

        Assert.Equal(asked, indexers.Any(indexer => indexer.Name == "Nyaa"));

        // The general indexers are asked either way: they name no library, so
        // they are for all of them.
        Assert.Contains(indexers, indexer => indexer.Name == "LimeTorrents");
    }

    /// <summary>An indexer scoped to anime, as the shipped catalogue scopes Nyaa.</summary>
    private static readonly SourceDefinition AnimeOnly =
        new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}")
        {
            Priority = 45,
            Libraries = [LibraryKinds.Anime],
        };

    /// <remarks>
    /// <strong>The one shipped site that names its torrents nowhere.</strong>
    /// Not on the listing, not on the row's own page: both carry a button and
    /// an id, and the magnet comes back from a signed request to the site's own
    /// endpoint. It was deferred from <c>S2-06</c> to <c>S6-01</c> and never
    /// written, and the cost was not that this site gave nothing — it was that
    /// it gave the <em>best</em> rows. It publishes honest seeder counts and
    /// sorts by them, so its copy outranked every other site's, was chosen, was
    /// followed, named no torrent, and the episode was reported as though
    /// nobody were serving it.
    /// </remarks>
    [Fact]
    public async Task ASiteThatPrintsNoTorrentIsAskedForOneAndAnswersWithIt()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("torrentbay.html"));

        RecordingPost post = new(
            """{"success":true,"url":"magnet:?xt=urn:btih:0123456789ABCDEF0123456789ABCDEF01234567&dn=Silo&tr=udp%3A%2F%2Ft.test%3A80%2Fannounce"}""");

        Find find = Finding(fetch, sources: [TorrentBay], post: post);

        ReleaseCopy row = (await find.AskAsync(TorrentBay, new(Name, true), null, CancellationToken.None))[0];

        // Nothing a client could be handed, which is what every row of this
        // site looks like.
        Assert.Null(row.Magnet);
        Assert.Null(row.InfoHash);

        ReleaseCopy followed = await find.ResolveAsync(row, CancellationToken.None);

        Assert.StartsWith("magnet:?", followed.Magnet!, StringComparison.Ordinal);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", followed.InfoHash);
        Assert.Contains("udp://t.test:80/announce", followed.Trackers);

        // The request the site's own script would have made, to the host the
        // row came from.
        Assert.Equal("https://extranet.torrentbay.st/ajax/getSearchMagnet.php", post.Url!.ToString());
        Assert.Contains("torrent_id=21152668", post.Body!, StringComparison.Ordinal);
        Assert.Contains("sessid=0c01634dba9aa280bc08db6088889c8a", post.Body!, StringComparison.Ordinal);

        // And the row's own page was never fetched: it names no torrent either,
        // so asking for it is a request spent for certain on nothing.
        Assert.DoesNotContain(
            fetch.Asked,
            address => address.AbsolutePath.Contains("silo-s03e06", StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// A refusal is not a magnet. This site answers one with the same shape of
    /// body as a success, and reading it as an address would hand the client
    /// something that is not a torrent — while the copy that could have been
    /// taken instead went unexamined.
    /// </remarks>
    [Fact]
    public async Task ARefusalFromThatSiteLeavesTheCopyWithNoTorrent()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("torrentbay.html"));

        RecordingPost post = new("""{"success":false,"error":"Invalid or expired token."}""");

        Find find = Finding(fetch, sources: [TorrentBay], post: post);

        ReleaseCopy row = (await find.AskAsync(TorrentBay, new(Name, true), null, CancellationToken.None))[0];

        Assert.Null((await find.ResolveAsync(row, CancellationToken.None)).Magnet);
    }

    /// <remarks>
    /// With nothing that can post from inside the session, the copy is left as
    /// it is rather than guessed at. Sent from this process the request arrives
    /// without the session that earned the right to ask and is refused, and the
    /// caller needs to be free to try the next copy.
    /// </remarks>
    [Fact]
    public async Task WithNothingAbleToPostTheCopyIsLeftAlone()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("torrentbay.html"));

        Find find = Finding(fetch, sources: [TorrentBay]);

        ReleaseCopy row = (await find.AskAsync(TorrentBay, new(Name, true), null, CancellationToken.None))[0];

        Assert.Null((await find.ResolveAsync(row, CancellationToken.None)).Magnet);
    }

    /// <remarks>
    /// A listing that answers seventy-one results fifty to a page keeps the
    /// other twenty-one somewhere. The pages after the first are read for a
    /// site that declares it has them, and a page with nothing on it is the end
    /// — asking for the one after that is a request spent on a page nobody
    /// wrote.
    /// </remarks>
    [Fact]
    public async Task ASiteThatDeclaresMorePagesIsReadPastItsFirst()
    {
        FakeFetch fetch = new();

        // The name letter for letter is the first question, and it answers, so
        // the pages that follow are that question's.
        string first = Query.WriteExact(TorrentBay.SearchAddress!, Name);

        fetch.Answers(first, Capture.Fixture("torrentbay.html"));
        fetch.Answers($"{first}&page=2", Capture.Fixture("torrentbay.html"));
        fetch.Answers($"{first}&page=3", "<html><body>no rows here</body></html>");

        ReleaseCopy[] copies = await Finding(fetch, sources: [TorrentBay])
            .AskAsync(TorrentBay, new(Name, true), null, CancellationToken.None);

        Assert.Equal(3, fetch.Asked.Count(address => address.Host == "extranet.torrentbay.st"));

        // The third page had nothing on it, which is the end. The two that did
        // are the same capture served twice, so every row comes back twice.
        ReleaseCopy[] one = await Finding(new FakeFetch().Answers(first, Capture.Fixture("torrentbay.html")), sources: [TorrentBay with { Pages = 1 }])
            .AskAsync(TorrentBay with { Pages = 1 }, new(Name, true), null, CancellationToken.None);

        Assert.Equal(one.Length * 2, copies.Length);
    }

    /// <remarks>
    /// A site that declares no pages is asked once. Guessing at the parameter
    /// fetches page one again under another name and reads every row of it
    /// twice.
    /// </remarks>
    [Fact]
    public async Task ASiteThatDeclaresNoPagesIsAskedOnce()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("limetorrents.html"));

        await Finding(fetch, sources: [Indexers[0]])
            .AskAsync(Indexers[0], new(Name, false), null, CancellationToken.None);

        Assert.Single(fetch.Asked);
    }

    /// <remarks>
    /// <strong>The two sites whose magnet path had never been walked.</strong>
    /// EZTV and 1337x both publish nothing on the listing, so the row's own
    /// page is the only route — and in every cycle so far a reachable copy of
    /// the same release from somewhere else won first, so neither was ever
    /// followed. Both pages here are real, captured through the same gate and
    /// solver the plugin uses, and the hash asserted is the one each page
    /// carries.
    /// </remarks>
    [Theory]
    [InlineData("eztv-detail.html", "EZTV", "https://eztvx.to/ep/3141579/silo-s03e08-xvid-afg/?d=",
        "FED03CC5627432777F0F6B6A0D62E96D0549E543")]
    [InlineData("x1337-detail.html", "1337x",
        "https://www.1337x.to/torrent/6701056/Silo-S03E06-The-Drive-2160p-ATVP-WEB-DL-ITA-ENG-DDP5-1-Atmos-DV-HDR-H-265-G66-mkv/",
        "00784AF82A96D3B9600AED78BCB2B4B3D40932F3")]
    public async Task ARowFromASiteThatPublishesNothingIsFollowedToItsMagnet(
        string fixture,
        string site,
        string detail,
        string hash)
    {
        FakeFetch fetch = new();
        fetch.Answers(detail, Capture.Fixture(fixture));

        ReleaseCopy row = new(Name, site, 30, null, null, new(detail), 9);

        ReleaseCopy followed = await Finding(fetch).ResolveAsync(row, CancellationToken.None);

        Assert.Equal(hash, followed.InfoHash);
        Assert.StartsWith("magnet:?xt=urn:btih:", followed.Magnet!, StringComparison.OrdinalIgnoreCase);

        // And the trackers the page's own magnet names travel with it. They
        // arrive HTML-escaped on both of these pages, which is a shape no
        // client would announce to.
        Assert.NotEmpty(followed.Trackers);
        Assert.All(
            followed.Trackers,
            tracker => Assert.DoesNotContain("&amp;", tracker, StringComparison.Ordinal));
    }

    private const string Name = "Silo.S03E06.1080p.WEB.H264-CAKES";

    private const string Detail =
        "https://www.torrentdownloads.pro/torrent/1707086634/Sugar-S02E08-Like-Sugar-2160p-ATVP-WEB-DL-ITA-ENG-DD5-1-DV-HDR-H-265-G66-mkv";

    /// <summary>
    /// The site that publishes neither a magnet nor a hash, as the catalogue
    /// has it — paging and all.
    /// </summary>
    private static readonly SourceDefinition TorrentBay =
        new("TorrentBay", "site", "https://extranet.torrentbay.st/browse/?q={query}&sort=seeders&order=desc")
        {
            Reader = "torrentbay",
            Priority = 30,
            PageParameter = "page",
            Pages = 3,
        };

    /// <summary>Three real indexers, as the catalogue has them.</summary>
    private static readonly SourceDefinition[] Indexers =
    [
        new("LimeTorrents", "site", "https://www.limetorrents.lol/search/all/{query}/") { Priority = 35 },
        new("Torrentz2", "site", "https://torrentz2.nz/search?q={query}")
        {
            Reader = "torrentz2",
            Priority = 25,
        },
        new("TorrentDownloads", "site", "https://www.torrentdownloads.pro/search/?search={query}")
        {
            Reader = "torrentdownloads",
            Priority = 25,
        },
    ];

    private static string Address(string source)
    {
        SourceDefinition indexer = Indexers.Single(one => one.Name == source);

        return Query.Write(indexer.SearchAddress!, Name, indexer.Query);
    }

    /// <summary>Where the name goes first: letter for letter, as the source wrote it.</summary>
    private static string Exact(string source)
    {
        SourceDefinition indexer = Indexers.Single(one => one.Name == source);

        return Query.WriteExact(indexer.SearchAddress!, Name);
    }

    private static Find Finding(
        FakeFetch fetch,
        TimeProvider? clock = null,
        ActivityJournal? journal = null,
        ISourceLedger? ledger = null,
        IReadOnlyList<SourceDefinition>? sources = null,
        ISessionPost? post = null)
    {
        _ = clock;

        return new(
            SourceCatalogue.Build(sources ?? Indexers, [], []),
            fetch,
            Readers.Shipped(),
            journal ?? new ActivityJournal(),
            ledger,
            TimeProvider.System,
            post);
    }
}
