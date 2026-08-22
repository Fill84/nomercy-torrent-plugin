using System.Net;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Tools.SourceHealth;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Tools;

/// <summary>
/// The health tool's judgement, against pages the sources really sent.
/// </summary>
/// <remarks>
/// Every page here is a capture in <c>tests/fixtures/</c>. A health check
/// tested against invented markup would agree with itself and with nothing
/// else, which is how 0.3.4's managed to report its own rate-limiting as a
/// broken parser.
/// </remarks>
public class SourceHealthTests
{
    /// <remarks>
    /// <strong>G2, first half.</strong> 0.3.4's health check attributed one
    /// source's page to another: whoever held the body never let go of it, so a
    /// source that answered nothing at all was reported with the previous
    /// source's page underneath it — and the reader that "failed" on it was
    /// never asked that page in the first place. The clear happens before the
    /// ask, so there is no window in which a stale body can be read.
    /// </remarks>
    [Fact]
    public async Task ThePageOfASourceThatAnsweredIsNotAttributedToTheNextOne()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.OK, Fixture("limetorrents.html"))
            .Throws(new HttpRequestException("no route to host"));

        SourceCheck check = Checking(http);

        SourceHealthCheck answered = await check.RunAsync(Limetorrents, Term, CancellationToken.None);
        SourceHealthCheck silent = await check.RunAsync(Silent, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.Answering, answered.Condition);
        Assert.NotNull(answered.Page);

        Assert.Equal(SourceCondition.NoAnswer, silent.Condition);
        Assert.Null(silent.Page);

        // Not nought rows: nought is a page that was read and had nothing on
        // it, and this one was never read at all.
        Assert.Null(silent.Rows);
    }

    /// <remarks>
    /// <strong>G2, second half.</strong> And it reported its own rate-limiting
    /// as a broken parser. A 429 is this plugin asking too often, so the source
    /// is asked once more — the gate has already widened the gap for that host,
    /// so the second ask waits it out on the way in — and if it refuses again
    /// it is reported as rate-limited and not as broken.
    /// </remarks>
    [Fact]
    public async Task ARateLimitedSourceIsAskedAgainAndAnswersTheSecondTime()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.TooManyRequests)
            .Answers(HttpStatusCode.OK, Fixture("limetorrents.html"));

        SourceHealthCheck result = await Checking(http).RunAsync(Limetorrents, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.Answering, result.Condition);
        Assert.True(result.Retried);
        Assert.Equal(2, http.Attempts.Count);
    }

    /// <remarks>
    /// Twice refused is reported as being refused, in its own words. It is not
    /// a broken reader, not a dead site, and not something to take a fresh
    /// capture for: it is this plugin being told to slow down.
    /// </remarks>
    [Fact]
    public async Task ASourceThatRefusesTwiceIsReportedAsRateLimitedAndNotAsBroken()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.TooManyRequests)
            .Answers(HttpStatusCode.TooManyRequests);

        SourceHealthCheck result = await Checking(http).RunAsync(Limetorrents, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.RateLimited, result.Condition);
        Assert.True(result.Retried);
        Assert.Equal(2, http.Attempts.Count);
    }

    /// <remarks>
    /// The case the tool exists for. TorrentGalaxy's page really is covered in
    /// releases — and its rows are <c>tgxtablerow</c> divs, so a reader looking
    /// for table rows finds none of them. Zero rows off a page like this is the
    /// reader being wrong about the site, and saying "nothing found" would be
    /// the health check agreeing with the fault.
    /// </remarks>
    [Fact]
    public async Task APageCoveredInReleasesAndZeroRowsReadIsABrokenReader()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.OK, Fixture("torrentgalaxy.html"));

        // Its own reader, deliberately not named: this is the shape the fault
        // takes when a site changes its markup under a reader that used to work.
        SourceDefinition asIfItsMarkupChanged = new(
            "TorrentGalaxy",
            "site",
            "https://torrentgalaxy.one/get-posts/keywords:{query}/");

        SourceHealthCheck result = await Checking(http).RunAsync(asIfItsMarkupChanged, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.BrokenReader, result.Condition);
        Assert.Equal(0, result.Rows);
        Assert.True(result.Releases > PageReleases.Few, $"Only {result.Releases} releases seen on the page.");
    }

    /// <remarks>
    /// And the other side of it, which is the harder half: Nyaa answers nothing
    /// for anything that is not anime, and nothing is an answer. A tool that
    /// cannot tell this page from the one above cries wolf every cycle for
    /// every source, and one that cries wolf is one nobody reads.
    /// </remarks>
    [Fact]
    public async Task ASiteThatHonestlyHasNothingIsNotReportedAsBroken()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.OK, Fixture("nyaa-nothing.xml"));

        SourceDefinition nyaa = new("Nyaa", "torrent-rss", "https://nyaa.si/?page=rss&q={query}");

        SourceHealthCheck result = await Checking(http).RunAsync(nyaa, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.NothingToSay, result.Condition);
        Assert.Equal(0, result.Rows);
    }

    /// <remarks>
    /// <strong>C4.</strong> A source nothing can read is reported as exactly
    /// that. It is not a site with nothing to offer, and it is not a broken
    /// reader either: there is no reader.
    /// </remarks>
    [Fact]
    public async Task ASourceNoReaderAnswersToIsReportedAsHavingNone()
    {
        FakeHttp http = new FakeHttp().Answers(HttpStatusCode.OK, Fixture("limetorrents.html"));

        SourceDefinition unreadable = new(
            "LimeTorrents",
            "site",
            "https://www.limetorrents.lol/search/all/{query}/")
        {
            Reader = "a-reader-nobody-wrote",
        };

        SourceHealthCheck result = await Checking(http).RunAsync(unreadable, Term, CancellationToken.None);

        Assert.Equal(SourceCondition.NoReader, result.Condition);
    }

    /// <remarks>
    /// Rows with no way to a torrent are a source that cannot be downloaded
    /// from, which is worth exactly as much as one that answered nothing —
    /// TorrentBay produced rows like these for weeks and zero downloads. The
    /// rows here are real ones off srrDB's real answer, which carries names and
    /// no route to anything at all; that is right for a name database and is
    /// the fault in an indexer, so both are asserted.
    /// </remarks>
    [Fact]
    public void AnIndexerWhoseRowsOfferNoRouteToATorrentIsFlaggedAndANameDatabaseIsNot()
    {
        IReadOnlyList<SourceRow> names = new SrrdbReader()
            .Read(Fixture("srrdb-search.json"), new("https://api.srrdb.com/v1/search/silo"));

        Assert.NotEmpty(names);
        Assert.All(names, row => Assert.Null(row.DetailUrl));

        SourceDefinition srrdb = new("srrDB search", "srrdb", "https://api.srrdb.com/v1/search/{query}");

        Assert.Equal(SourceCondition.NoRoute, SourceCheck.Judge(Limetorrents, names, releases: 20));
        Assert.Equal(SourceCondition.Answering, SourceCheck.Judge(srrdb, names, releases: 20));
    }

    /// <remarks>
    /// The report says which page belongs to which source and writes each one
    /// out beside it, because repairing a reader means reading the page it
    /// failed on. A source that returned nothing gets no file and says so in
    /// words: an empty file would read as a site that answered with nothing.
    /// </remarks>
    /// <remarks>
    /// <para>
    /// A reader that has stopped seeing <em>some</em> of a page is the fault
    /// this rule is for. Nought rows is already a broken reader and says so
    /// loudly; three rows where there were forty last week is a site that
    /// changed half its markup, and every condition before this one calls that
    /// "answering".
    /// </para>
    /// <para>
    /// Against the last run and not against a number written here: what a
    /// search really returns depends on the term and the day, and a figure
    /// chosen by hand would be wrong for every source but the one it was
    /// measured on.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASourceAnsweringWithFewerRowsThanLastTimeIsFlagged()
    {
        HealthBaseline was = new(new Dictionary<string, int> { ["1337x"] = 40 });

        SourceHealthCheck fewer = HealthBaseline.Judge(Answered("1337x", rows: 3), was);

        Assert.True(fewer.Flagged);
        Assert.Equal(SourceCondition.FewerRows, fewer.Condition);

        // Both numbers, or the owner cannot tell a site that dropped two rows
        // from one that dropped every row but three.
        Assert.Contains("3", fewer.Detail, StringComparison.Ordinal);
        Assert.Contains("40", fewer.Detail, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The same count, or more, is the ordinary case and is left alone. A rule
    /// that flagged a steady source would be one nobody reads the output of.
    /// </remarks>
    [Theory]
    [InlineData(40)]
    [InlineData(41)]
    public void ASourceAnsweringWithAsManyRowsOrMoreIsLeftAlone(int rows)
    {
        HealthBaseline was = new(new Dictionary<string, int> { ["1337x"] = 40 });

        Assert.False(HealthBaseline.Judge(Answered("1337x", rows), was).Flagged);
    }

    /// <remarks>
    /// A source with no baseline has nothing to be fewer than. The first run
    /// after a source is added would otherwise flag it for having no history.
    /// </remarks>
    [Fact]
    public void ASourceNobodyHasABaselineForIsNotFlaggedForIt()
    {
        Assert.False(HealthBaseline.Judge(Answered("brand-new", rows: 1), new(new Dictionary<string, int>())).Flagged);
    }

    /// <remarks>
    /// <para>
    /// Only what answered is written down. A source that was rate-limited
    /// answered no rows at all, and a broken reader answered nought off a page
    /// covered in releases — and nought is a number.
    /// </para>
    /// <para>
    /// Writing that down would set the baseline to nought, and every run after
    /// it would find nought rows to be no fewer than last time. The rule would
    /// then never fire again for the one source it had just caught.
    /// </para>
    /// </remarks>
    [Fact]
    public void OnlyWhatAnsweredIsWrittenIntoTheNextBaseline()
    {
        HealthBaseline next = HealthBaseline.Of(
        [
            Answered("1337x", rows: 40),
            Refused("TorrentBay"),
            Broken("EZTV"),
        ]);

        Assert.Equal(40, next.Rows["1337x"]);
        Assert.DoesNotContain("TorrentBay", next.Rows.Keys);
        Assert.DoesNotContain("EZTV", next.Rows.Keys);
    }

    /// <remarks>
    /// It is run by a person and by whatever they wire it into. An exit code of
    /// nought with a report full of broken readers is a check that cannot fail,
    /// and a check that cannot fail is one nobody acts on.
    /// </remarks>
    [Fact]
    public void TheToolFailsWhenAnythingIsBrokenAndPassesWhenNothingIs()
    {
        Assert.Equal(0, HealthBaseline.ExitCode([Answered("1337x", rows: 40)]));
        Assert.Equal(1, HealthBaseline.ExitCode([Answered("1337x", rows: 40), Refused("TorrentBay")]));
    }

    /// <summary>A source that answered with this many rows.</summary>
    private static SourceHealthCheck Answered(string name, int rows)
    {
        return new(
            new(name, "site", $"https://{name}.example/search/{{query}}"),
            SourceCondition.Answering,
            $"https://{name}.example/search/silo",
            rows,
            rows,
            Retried: false,
            Page: "<html></html>",
            $"{rows} rows");
    }

    /// <summary>One whose page is covered in releases and whose reader saw none.</summary>
    private static SourceHealthCheck Broken(string name)
    {
        return new(
            new(name, "site", $"https://{name}.example/search/{{query}}"),
            SourceCondition.BrokenReader,
            $"https://{name}.example/search/silo",
            Rows: 0,
            Releases: 30,
            Retried: false,
            Page: "<html>lots of releases</html>",
            "nought rows off a page covered in releases");
    }

    /// <summary>One that was asked twice and refused both times.</summary>
    private static SourceHealthCheck Refused(string name)
    {
        return new(
            new(name, "site", $"https://{name}.example/search/{{query}}"),
            SourceCondition.RateLimited,
            $"https://{name}.example/search/silo",
            Rows: null,
            Releases: null,
            Retried: true,
            Page: null,
            "refused twice");
    }

    [Fact]
    public async Task TheReportWritesEachSourcesOwnPageAndSaysSoWhenThereIsNone()
    {
        FakeHttp http = new FakeHttp()
            .Answers(HttpStatusCode.OK, Fixture("limetorrents.html"))
            .Throws(new HttpRequestException("no route to host"));

        SourceCheck check = Checking(http);

        SourceHealthCheck[] checks =
        [
            await check.RunAsync(Limetorrents, Term, CancellationToken.None),
            await check.RunAsync(Silent, Term, CancellationToken.None),
        ];

        string folder = Path.Combine(Path.GetTempPath(), "nomercy-torrent-tests", Guid.NewGuid().ToString("n"));

        try
        {
            string report = HealthReport.Write(checks, folder, Term, new DateTimeOffset(2026, 8, 15, 9, 0, 0, TimeSpan.Zero));

            string written = File.ReadAllText(report);

            Assert.Contains("LimeTorrents", written, StringComparison.Ordinal);
            Assert.Contains("limetorrents.html", written, StringComparison.Ordinal);

            // The page really is beside the report, and it is LimeTorrents' own.
            string page = File.ReadAllText(Path.Combine(folder, "limetorrents.html"));
            Assert.Equal(Fixture("limetorrents.html"), page);

            // And the source that answered nothing has no page at all, neither
            // its own nor anybody else's.
            Assert.Empty(Directory.GetFiles(folder, "silent*"));
            Assert.Contains("no page", written, StringComparison.OrdinalIgnoreCase);

            // Nor a number of rows. Nought would say it was read and had
            // nothing on it, which is a different source and a different thing
            // to do about it.
            Assert.Contains("not read", written, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    /// <remarks>
    /// The count that separates the two, measured on the pages themselves. Every
    /// captured listing is covered in releases; the one capture of a site
    /// answering nothing has none. The threshold is above one because a page
    /// that found nothing still prints the term that was searched for.
    /// </remarks>
    [Theory]
    [InlineData("1337x.html")]
    [InlineData("eztv.html")]
    [InlineData("limetorrents.html")]
    [InlineData("torrentbay.html")]
    [InlineData("torrentdownloads.html")]
    [InlineData("torrentgalaxy.html")]
    [InlineData("torrentz2.html")]
    [InlineData("nyaa.xml")]
    [InlineData("predb.xml")]
    [InlineData("scenesource.xml")]
    [InlineData("srrdb.xml")]
    [InlineData("srrdb-search.json")]
    [InlineData("the-pirate-bay.json")]
    [InlineData("eztv-latest.json")]
    public void EveryCapturedPageWithReleasesOnItIsSeenToHaveThem(string fixture)
    {
        Assert.True(
            PageReleases.CountIn(Fixture(fixture)) > PageReleases.Few,
            $"{fixture} has releases on it and the count saw none.");
    }

    /// <remarks>
    /// And the page of a site that honestly answered nothing has none — the
    /// assertion this whole count exists to be able to make.
    /// </remarks>
    [Fact]
    public void ThePageOfASiteWithNothingToSayIsSeenToHaveNothingOnIt()
    {
        Assert.True(PageReleases.CountIn(Fixture("nyaa-nothing.xml")) <= PageReleases.Few);
    }

    private const string Term = "Silo S03E06";

    private static readonly SourceDefinition Limetorrents = new(
        "LimeTorrents",
        "site",
        "https://www.limetorrents.lol/search/all/{query}/");

    /// <summary>A source at a host that will not answer at all.</summary>
    private static readonly SourceDefinition Silent = new(
        "Silent",
        "site",
        "https://silent.example/search/{query}/");

    /// <summary>
    /// The real fetch, not a stand-in: the clearing this tests is of the body
    /// that fetch holds, and a fake would clear a field nothing reads.
    /// </summary>
    /// <remarks>
    /// The real clock with no interval at all, because a test that asks one
    /// host twice cannot use a fake one — the second request waits on a gap
    /// nothing will advance.
    /// </remarks>
    private static SourceCheck Checking(FakeHttp http)
    {
        FakeGrants grants = new();
        grants.Grant("www.limetorrents.lol");
        grants.Grant("torrentgalaxy.one");
        grants.Grant("nyaa.si");
        grants.Grant("silent.example");

        HostGate gate = new(TimeProvider.System);

        foreach (string host in (string[])["www.limetorrents.lol", "torrentgalaxy.one", "nyaa.si", "silent.example"])
        {
            gate.Configure(host, TimeSpan.Zero);
        }

        return new(new(http.Client(), gate, grants, new ClearanceStore()), Readers.Shipped());
    }

    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllText(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}
