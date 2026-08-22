using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

/// <summary>
/// The sources that answer data rather than a page, each against what it really
/// sent on 15 August 2026.
/// </summary>
public class DataReaderTests
{
    /// <remarks>
    /// The website is a JavaScript shell with no results in it; this endpoint
    /// is what it calls, and it answers an info hash and honest seeder counts.
    /// </remarks>
    [Fact]
    public void ApibayAnswersHashesAndSeeders()
    {
        IReadOnlyList<SourceRow> rows = new ApibayReader().Read(
            Fixture("the-pirate-bay.json"),
            new("https://apibay.org/q.php?q=Silo+S03E06&cat="));

        Assert.NotEmpty(rows);
        Assert.Equal("Silo S03E06 1080p WEB H264-CAKES", rows[0].Title);
        Assert.Equal("92D8A3F6864911EF292B4BE0DD5286406396D2B3", rows[0].InfoHash);
        Assert.Equal(4024, rows[0].Seeders);
        Assert.Equal(1793, rows[0].Leechers);
        Assert.Equal(4388742440, rows[0].SizeBytes);
    }

    /// <remarks>
    /// It says "nothing found" as a single row saying so, rather than as an
    /// empty array. Taking that row puts a release called "No results returned"
    /// into the name pool, where it would be searched for on every indexer.
    /// </remarks>
    [Fact]
    public void ApibayTakesNoRowFromItsWayOfSayingNothing()
    {
        Assert.Empty(new ApibayReader().Read(
            $$"""[{"id":"0","name":"{{ApibayReader.NothingFound}}","info_hash":"0000000000000000000000000000000000000000","seeders":"0","leechers":"0","size":"0"}]""",
            new("https://apibay.org/q.php?q=nothing&cat=")));
    }

    /// <remarks>
    /// EZTV's endpoint publishes the magnet outright, which is the one shipped
    /// source that does.
    /// </remarks>
    [Fact]
    public void TheEztvEndpointAnswersMagnetsOutright()
    {
        IReadOnlyList<SourceRow> rows = new EztvApiReader().Read(
            Fixture("eztv-latest.json"),
            new("https://eztv.re/api/get-torrents?limit=100"));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.StartsWith("magnet:", row.Magnet ?? string.Empty, StringComparison.Ordinal));
        Assert.All(rows, row => Assert.NotNull(row.InfoHash));
        Assert.Equal(
            "The.Young.and.the.Restless.S53E217.XviD-AFG[EZTVx.to].avi",
            rows[0].Title);
    }

    /// <remarks>
    /// Names, not torrents, and that is the point: it says what an episode from
    /// three weeks ago is actually called, and only a name like that is fit to
    /// put to an indexer.
    /// </remarks>
    [Fact]
    public void SrrdbAnswersNamesAndNoTorrents()
    {
        IReadOnlyList<SourceRow> rows = new SrrdbReader().Read(
            Fixture("srrdb-search.json"),
            new("https://api.srrdb.com/v1/search/silo-s03e06"));

        Assert.NotEmpty(rows);
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", rows[0].Title);
        Assert.All(rows, row => Assert.Null(row.Magnet));
        Assert.All(rows, row => Assert.Null(row.InfoHash));
    }

    /// <remarks>
    /// A show with no scene releases honestly answers zero, and zero is an
    /// answer rather than a broken reader. The health tool has to tell those
    /// two apart, and this is the shape it must not flag.
    /// </remarks>
    [Fact]
    public void SrrdbAnsweringZeroIsAnAnswerAndNotAFailure()
    {
        IReadOnlyList<SourceRow> rows = new SrrdbReader().Read(
            """{"results":[],"resultsCount":"0","warnings":[],"query":["nothing-at-all"]}""",
            new("https://api.srrdb.com/v1/search/nothing-at-all"));

        Assert.Empty(rows);
    }

    /// <remarks>
    /// srrDB writes every dash in a release name as an entity, so
    /// <c>Persiana_Jones&amp;#45;Una_Vita</c> matches nothing at all until it
    /// is decoded — and a scene name is mostly dashes.
    /// </remarks>
    [Fact]
    public void AFeedsEncodedDashesAreDecodedBackIntoTheName()
    {
        IReadOnlyList<SourceRow> rows = new RssNameReader().Read(
            Fixture("srrdb.xml"),
            new("https://www.srrdb.com/feed/srrs"));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.DoesNotContain("&#", row.Title, StringComparison.Ordinal));
        Assert.Contains(rows, row => row.Title.Contains('-', StringComparison.Ordinal));
        Assert.Equal("Persiana_Jones-Una_Vita_Fantastica-WEB-IT-2023-SDR", rows[0].Title);
    }

    /// <remarks>
    /// <para>
    /// SceneSource has a search, and it is the same RSS the feed is:
    /// <c>/feed/?s={query}</c> answers "You searched for X" with an item per
    /// release. The catalogue said it had none, so a show was left to srrDB's
    /// archive — which answers with years of foreign and 2160p releases and
    /// almost never the one that aired last week.
    /// </para>
    /// <para>
    /// Off the real search, captured through the browser on 22 August 2026:
    /// the address is behind a challenge and answers a plain request with 403.
    /// </para>
    /// </remarks>
    [Fact]
    public void SceneSourcesSearchAnswersTheEpisodesOfTheShowAskedFor()
    {
        IReadOnlyList<SourceRow> rows = new RssNameReader().Read(
            Fixture("scenesource-search-silo.xml"),
            new("https://www.scnsrc.me/feed/?s=Silo"));

        IReadOnlyList<string> titles = [.. rows.Select(row => row.Title)];

        Assert.Contains("Silo S03E08 1080p WEB H264-CAKES", titles);
        Assert.Contains("Silo S03E01 1080p WEB h264-ETHEL", titles);

        // Eight episodes of the season the owner is missing, in one ask.
        Assert.Equal(8, titles.Count(title => title.StartsWith("Silo S03E", StringComparison.Ordinal)));
    }

    /// <remarks>
    /// A feed answers what came out recently, and every item is a name.
    /// </remarks>
    [Theory]
    [InlineData("predb.xml", "https://predb.me/?rss=1")]
    [InlineData("scenesource.xml", "https://www.scnsrc.me/feed/")]
    public void AFeedAnswersNames(string fixture, string address)
    {
        IReadOnlyList<SourceRow> rows = new RssNameReader().Read(Fixture(fixture), new(address));

        Assert.NotEmpty(rows);
        Assert.All(rows, row => Assert.NotEmpty(row.Title));
        Assert.All(rows, row => Assert.NotNull(row.DetailUrl));
    }

    /// <remarks>
    /// Nyaa is an indexer in XML: every item links a real torrent and carries
    /// its hash. For anime it is often the only source that has the release.
    /// </remarks>
    [Fact]
    public void NyaaAnswersTorrentsWithTheirHashes()
    {
        IReadOnlyList<SourceRow> rows = new TorrentRssReader().Read(
            Fixture("nyaa.xml"),
            new("https://nyaa.si/?page=rss&q=Frieren+S01E13"));

        Assert.NotEmpty(rows);
        Assert.Equal("C5B9337296E01CA2C1CC6D7938451F49C47011F2", rows[0].InfoHash);
        Assert.Equal(28, rows[0].Seeders);
        Assert.Contains("Frieren", rows[0].Title, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And it answers nothing for anything that is not anime, which is an
    /// answer. The capture is Nyaa asked for a live-action show.
    /// </remarks>
    [Fact]
    public void NyaaAnsweringNothingIsAnAnswer()
    {
        Assert.Empty(new TorrentRssReader().Read(
            Fixture("nyaa-nothing.xml"),
            new("https://nyaa.si/?page=rss&q=Silo+S03E06")));
    }

    /// <remarks>
    /// A body that is not JSON at all — a challenge page, an outage notice —
    /// answers no rows rather than throwing. Whether that is a fault is the
    /// health tool's judgement, not the reader's.
    /// </remarks>
    [Fact]
    public void ABodyThatIsNotWhatWasExpectedAnswersNothingRatherThanThrowing()
    {
        Uri from = new("https://apibay.org/q.php?q=x");

        Assert.Empty(new ApibayReader().Read("<html>Just a moment...</html>", from));
        Assert.Empty(new EztvApiReader().Read("not json at all", from));
        Assert.Empty(new SrrdbReader().Read(string.Empty, from));
        Assert.Empty(new RssNameReader().Read("<html><body>no feed here</body></html>", from));
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
