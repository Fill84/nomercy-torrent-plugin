using Microsoft.Extensions.Time.Testing;
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
/// Asking every indexer who is serving one release.
/// </summary>
/// <remarks>
/// Every page here is one a site really sent, detail pages included: those were
/// captured through the same tool, at the address a row on the listing really
/// carried.
/// </remarks>
public class FindTests
{
    /// <remarks>
    /// Whatever term this stage is handed goes out whole, to every indexer,
    /// written the way each one wants it. Which terms are worth asking is
    /// <c>SearchCycle</c>'s business and not this one's — see its own tests,
    /// and the correction to <strong>A3</strong> they carry.
    /// </remarks>
    [Fact]
    public async Task EveryIndexerIsAskedTheTermItWasGivenWhole()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("limetorrents.html"));

        await Finding(fetch).SearchAsync("Silo.S03E06.1080p.WEB.H264-CAKES", LibraryKind.Television, CancellationToken.None);

        Assert.NotEmpty(fetch.Asked);

        // The parts only the full name has, whichever way a site wants its
        // terms written: one joins them with plus signs, one with dashes.
        foreach (string part in (string[])["1080p", "cakes"])
        {
            Assert.All(
                fetch.Asked,
                address => Assert.Contains(part, Uri.UnescapeDataString(address.ToString()), StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <remarks>
    /// Every indexer at once. Asked one after another, a search costs the sum
    /// of the slowest sites and there are one per episode of a cycle.
    /// </remarks>
    [Fact]
    public async Task EveryIndexerIsAskedAtOnceRatherThanOneAfterAnother()
    {
        FakeTimeProvider clock = new();
        FakeFetch fetch = new(clock);

        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"), TimeSpan.FromSeconds(5));
        fetch.Answers(Address("Torrentz2"), Capture.Fixture("torrentz2.html"), TimeSpan.FromSeconds(3));
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"), TimeSpan.FromSeconds(1));

        Task run = Finding(fetch, clock).SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        await Task.WhenAny(fetch.AllInFlight, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            fetch.AllInFlight.IsCompletedSuccessfully,
            $"Only {fetch.InFlight} of {fetch.Expected} indexers were in flight, so they were asked one at a time.");

        clock.Advance(TimeSpan.FromSeconds(5));

        await run;
    }

    /// <remarks>
    /// <para>
    /// <strong>One hash is one torrent, whatever a site calls it.</strong> The
    /// info hash is the identity: two rows carrying it are the same bytes in
    /// the same swarm, and a site that writes the year in while another leaves
    /// it out has not found a different file.
    /// </para>
    /// <para>
    /// Merging ran by name first, so rows that named the same hash differently
    /// stayed apart and their trackers were never put together. On 26 August
    /// 2026 two Lioness episodes sat at "fetching metadata" with no peer and no
    /// seed for hours, while the same release was seeding perfectly through a
    /// tracker only the other row published.
    /// </para>
    /// </remarks>
    [Fact]
    public void TheSameHashUnderTwoDifferentNamesIsStillOneTorrent()
    {
        const string Hash = "6C4B2D8A1E5F9037C2A84B16D9E70F3B5A82C41D";

        ReleaseCopy[] copies =
        [
            new("Lioness 2023 S03E03 1080p WEB H264-CAKES", "EZTV", 40, Hash,
                $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fone.example%3A80", null, 3, 1_000),
            new("Lioness S03E03 1080p WEB H264-CAKES", "TorrentBay", 30, Hash,
                $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Ftwo.example%3A80", null, 25, 1_000),
        ];

        ReleaseCopy merged = Assert.Single(Find.Merge(copies));

        Assert.Equal(["udp://one.example:80", "udp://two.example:80"], merged.Trackers.Order());
        Assert.Equal(25, merged.Seeders);
    }

    /// <remarks>
    /// The same torrent on five sites is one torrent with five sets of
    /// trackers. More trackers is a faster download, which is the whole reason
    /// every indexer is asked rather than the first one that answers.
    /// </remarks>
    [Fact]
    public void TheSameHashFromEverySiteIsOneReleaseWithEveryTrackerAndTheBestCount()
    {
        const string Hash = "92D8A3F6864911EF292B4BE0DD5286406396D2B3";

        ReleaseCopy[] copies =
        [
            new(Name, "LimeTorrents", 35, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fone.example%3A80", null, 4, 1_000),
            new(Name, "The Pirate Bay", 45, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Ftwo.example%3A80", null, 40, 1_000),
            new(Name, "Torrentz2", 25, Hash, $"magnet:?xt=urn:btih:{Hash}&tr=udp%3A%2F%2Fone.example%3A80", null, 12, 1_000),
        ];

        ReleaseCopy merged = Assert.Single(Find.Merge(copies));

        Assert.Equal(40, merged.Seeders);
        Assert.Equal("The Pirate Bay", merged.Source);
        Assert.Equal(["udp://one.example:80", "udp://two.example:80"], merged.Trackers.Order());
    }

    /// <remarks>
    /// <strong>Corrected 22 August 2026.</strong> This asserted that two rows
    /// with the same name and no hash are two torrents, on the grounds that
    /// nothing says they are one. A scene release name says it: the group, the
    /// resolution and the source are all in it. Holding them apart is what let
    /// one site's count refuse a release the rest of the world was seeding in
    /// the thousands — see
    /// <see cref="CopiesOfOneReleaseAreOneTorrentAndKeepTheBestCountAnySiteGave"/>.
    ///
    /// The care it was written for is kept where it belongs: two rows carrying
    /// two <em>different</em> hashes really are two files, and they still stay
    /// two.
    /// </remarks>
    [Fact]
    public void TwoRowsOfOneReleaseWithNoHashAreOneTorrent()
    {
        ReleaseCopy[] copies =
        [
            new(Name, "TorrentGalaxy", 30, null, null, new("https://torrentgalaxy.one/1"), 9),
            new(Name, "Torrentz2", 25, null, null, new("https://torrentz2.nz/2"), 40),
        ];

        ReleaseCopy one = Assert.Single(Find.Merge(copies));

        Assert.Equal(40, one.Seeders);
        Assert.Equal("Torrentz2", one.Source);
    }

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

        ReleaseCopy followed = await Finding(fetch).FollowAsync(chosen, CancellationToken.None);

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

        ReleaseCopy followed = await Finding(fetch).FollowAsync(
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

        Assert.Same(already, await Finding(fetch).FollowAsync(already, CancellationToken.None));
        Assert.Empty(fetch.Asked);
    }

    /// <remarks>
    /// And a copy that carries a hash needs no page at all: a hash is
    /// everything a magnet is made of. LimeTorrents publishes a hashed
    /// <c>.torrent</c> link on every row and no magnet anywhere, so following
    /// those pages would be one request per grab for a torrent already in hand.
    /// </remarks>
    [Fact]
    public async Task ACopyThatCarriesAHashIsNotFollowedEither()
    {
        FakeFetch fetch = new();

        ReleaseCopy hashed = new(
            Name,
            "LimeTorrents",
            35,
            "92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            null,
            new(Detail),
            12);

        ReleaseCopy answered = await Finding(fetch).FollowAsync(hashed, CancellationToken.None);

        Assert.Empty(fetch.Asked);
        Assert.StartsWith(
            "magnet:?xt=urn:btih:92D8A3F6864911EF292B4BE0DD5286406396D2B3",
            answered.Magnet,
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// <strong>B4.</strong> Between two acceptable copies the higher-rated
    /// indexer wins, asserted with priorities that differ. 0.3.4 sorted the
    /// other way and took the worst-rated site every time two copies were level
    /// on seeders, which is most of the time — and a test enshrined it.
    /// </remarks>
    [Fact]
    public async Task BetweenTwoAcceptableCopiesTheHigherRatedIndexerWins()
    {
        FakeFetch fetch = new();
        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"));

        IReadOnlyList<ReleaseCopy> copies = await Finding(fetch).SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        // Both sites answered, and the ranking is the decider's own.
        Assert.Contains(copies, copy => copy.Source == "LimeTorrents");
        Assert.Contains(copies, copy => copy.Source == "TorrentDownloads");

        ReleaseCopy[] level =
        [
            .. copies
                .Where(copy => copy.Source is "LimeTorrents" or "TorrentDownloads")
                .Take(1)
                .Select(copy => copy with { Seeders = 10, Source = "TorrentDownloads", Priority = 25 }),
            .. copies
                .Where(copy => copy.Source is "LimeTorrents" or "TorrentDownloads")
                .Take(1)
                .Select(copy => copy with { Seeders = 10, Source = "LimeTorrents", Priority = 35 }),
        ];

        Decision decision = new ReleaseDecider(new() { MinimumSeeders = 2 }).Decide(level, Blacklist.None);

        Assert.Equal("LimeTorrents", decision.Chosen!.Source);
    }

    /// <remarks>
    /// One indexer being down is one indexer being down. Every other site still
    /// answers, and the episode is not lost because a site was.
    /// </remarks>
    [Fact]
    public async Task OneIndexerThatFailsDoesNotTakeTheSearchDown()
    {
        FakeFetch fetch = new();
        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.FailsHost("torrentz2.nz", FetchOutcome.RateLimited, "torrentz2.nz answered 429");
        fetch.Answers(Address("TorrentDownloads"), Capture.Fixture("torrentdownloads.html"));

        ActivityJournal journal = new();

        IReadOnlyList<ReleaseCopy> copies = await Finding(fetch, journal: journal)
            .SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        Assert.NotEmpty(copies);
        Assert.DoesNotContain(copies, copy => copy.Source == "Torrentz2");

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

        fetch.Answers(Address("LimeTorrents"), Capture.Fixture("limetorrents.html"));
        fetch.Fails(Address("Torrentz2"), FetchOutcome.RateLimited, "429 Too Many Requests");
        fetch.Fails(Address("TorrentDownloads"), FetchOutcome.Refused, "403 Forbidden");

        await Finding(fetch, ledger: ledger).SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        SourceAnswer answered = ledger.Answers.Single(one => one.Name == "LimeTorrents");

        Assert.True(answered.Rows > 0, "The captured page is covered in releases.");
        Assert.Null(answered.Refusal);

        SourceAnswer refused = ledger.Answers.Single(one => one.Name == "Torrentz2");

        // Its own words. "Broken" would be this plugin's judgement of a site
        // that simply asked to be left alone for a while, which is G2 exactly.
        Assert.Equal(0, refused.Rows);
        Assert.Contains("429", refused.Refusal!, StringComparison.Ordinal);
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
    public async Task ASourceIsAskedOnlyAboutTheLibrariesItNames(LibraryKind kind, bool asked)
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa.xml"));

        await Finding(fetch, sources: [.. Indexers, AnimeOnly]).SearchAsync(Name, kind, CancellationToken.None);

        Assert.Equal(
            asked,
            fetch.Asked.Any(address => address.Host.Contains("nyaa", StringComparison.OrdinalIgnoreCase)));

        // The general indexers are asked either way: they name no library, so
        // they are for all of them.
        Assert.Contains(fetch.Asked, address => address.Host.Contains("limetorrents", StringComparison.OrdinalIgnoreCase));
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

        ReleaseCopy row = (await find.SearchAsync(Name, LibraryKind.Television, CancellationToken.None))[0];

        // Nothing a client could be handed, which is what every row of this
        // site looks like.
        Assert.Null(row.Magnet);
        Assert.Null(row.InfoHash);

        ReleaseCopy followed = await find.FollowAsync(row, CancellationToken.None);

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

        ReleaseCopy row = (await find.SearchAsync(Name, LibraryKind.Television, CancellationToken.None))[0];

        Assert.Null((await find.FollowAsync(row, CancellationToken.None)).Magnet);
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

        ReleaseCopy row = (await find.SearchAsync(Name, LibraryKind.Television, CancellationToken.None))[0];

        Assert.Null((await find.FollowAsync(row, CancellationToken.None)).Magnet);
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

        string first = Query.Write(TorrentBay.SearchAddress!, Name, TorrentBay.Query);

        fetch.Answers(first, Capture.Fixture("torrentbay.html"));
        fetch.Answers($"{first}&page=2", Capture.Fixture("torrentbay.html"));
        fetch.Answers($"{first}&page=3", "<html><body>no rows here</body></html>");

        IReadOnlyList<ReleaseCopy> copies = await Finding(fetch, sources: [TorrentBay])
            .SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        Assert.Equal(3, fetch.Asked.Count(address => address.Host == "extranet.torrentbay.st"));

        // The third page had nothing on it, which is the end. The two that did
        // are the same capture served twice here, so what comes back is one
        // torrent per release rather than two of each: the merge answers for
        // that, and this test answers for the pages being asked at all.
        Assert.NotEmpty(copies);
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
            .SearchAsync(Name, LibraryKind.Television, CancellationToken.None);

        Assert.Single(fetch.Asked);
    }


    /// <remarks>
    /// <strong>One site's count is not the swarm's.</strong> On the owner's own
    /// library on 22 August 2026 TorrentBay offered
    /// <c>Sugar (2024) S02E08 1080p Web h264 Cakes</c> and said one seeder, so
    /// it was refused for being below the minimum — while the same release was
    /// seeded in the thousands everywhere else. It could not be rescued by any
    /// of them because it carried no info hash, and copies were only ever
    /// merged by hash.
    ///
    /// A scene release name <em>is</em> the file's identity: the group, the
    /// resolution and the source are all in it. Two rows carrying that name are
    /// one torrent, and how many are serving it is a property of the swarm
    /// rather than of the site that was asked.
    /// </remarks>
    [Fact]
    public void CopiesOfOneReleaseAreOneTorrentAndKeepTheBestCountAnySiteGave()
    {
        IReadOnlyList<ReleaseCopy> merged = Find.Merge(
        [
            // How TorrentBay prints it, with no route to the torrent at all.
            new("Sugar (2024) S02E08 1080p Web h264 Cakes", "TorrentBay", 30, Seeders: 1),

            // And how the scene named it, on a site that publishes the hash.
            new(
                "Sugar.2024.S02E08.1080p.WEB.H264-CAKES",
                "The Pirate Bay",
                45,
                InfoHash: "0123456789ABCDEF0123456789ABCDEF01234567",
                Seeders: 3968),
        ]);

        ReleaseCopy one = Assert.Single(merged);

        Assert.Equal(3968, one.Seeders);

        // And the copy that survives is the one anything can be downloaded
        // from, named by the site that knew the most about it.
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF01234567", one.InfoHash);
        Assert.Equal("The Pirate Bay", one.Source);
    }

    /// <remarks>
    /// The count belongs to the swarm, so the copy that can actually be reached
    /// keeps it even when the highest number came off a site with no route to
    /// the torrent. Without that, the best-informed row wins the ranking and
    /// then names nothing to download.
    /// </remarks>
    [Fact]
    public void TheReachableCopyKeepsTheCountEvenWhenAnotherSiteGaveIt()
    {
        IReadOnlyList<ReleaseCopy> merged = Find.Merge(
        [
            new("Silo.S03E08.1080p.WEB.H264-CAKES", "TorrentBay", 30, Seeders: 6092),
            new(
                "Silo S03E08 1080p WEB H264-CAKES",
                "LimeTorrents",
                35,
                InfoHash: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA",
                Seeders: 12),
        ]);

        ReleaseCopy one = Assert.Single(merged);

        Assert.Equal(6092, one.Seeders);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", one.InfoHash);
    }

    /// <remarks>
    /// Two hashes under one name are two files, whatever they are called, and
    /// merging them would hand one torrent's trackers to another. The old rule
    /// held for exactly this case and still does.
    /// </remarks>
    [Fact]
    public void TwoDifferentTorrentsUnderOneNameStayTwo()
    {
        IReadOnlyList<ReleaseCopy> merged = Find.Merge(
        [
            new("Silo.S03E08.1080p.WEB.H264-CAKES", "The Pirate Bay", 45, InfoHash: new('A', 40), Seeders: 10),
            new("Silo.S03E08.1080p.WEB.H264-CAKES", "LimeTorrents", 35, InfoHash: new('B', 40), Seeders: 20),
        ]);

        Assert.Equal(2, merged.Count);
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

        ReleaseCopy followed = await Finding(fetch).FollowAsync(row, CancellationToken.None);

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

    private static Find Finding(
        FakeFetch fetch,
        TimeProvider? clock = null,
        ActivityJournal? journal = null,
        ISourceLedger? ledger = null,
        IReadOnlyList<SourceDefinition>? sources = null,
        IInPagePost? post = null)
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
