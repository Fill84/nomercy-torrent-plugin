using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Sources;

/// <summary>
/// When an indexer says a torrent was uploaded, read off the page it really sent.
/// </summary>
/// <remarks>
/// <para>
/// Two programmes are called Dark Matter, one from 2015 and the owner's from 2024, and an indexer's row
/// carries no year to tell them apart. On 17 September 2026 the 2015 programme's S02E04 remux was grabbed for
/// the 2024 programme's S02E04, which aired that day. When a torrent was uploaded is what cannot be the same.
/// </para>
/// <para>
/// Only a date the page writes out is read. LimeTorrents, EZTV's listing and TorrentGalaxy print an age —
/// <c>1 Year+</c>, <c>1 year</c>, <c>21 hours, 42 minutes</c> — which is a year wide at the old end, where
/// it matters, and says nothing without the moment the page was served. TorrentDownloads prints no date.
/// </para>
/// </remarks>
public class UploadDateTests
{
    /// <remarks>apibay writes the moment as a unix timestamp in a string: <c>"added":"1731637437"</c>.</remarks>
    [Fact]
    public void ThePirateBayGivesTheMomentATorrentWasAdded()
    {
        IReadOnlyList<SourceRow> rows = new ApibayReader().Read(
            Capture.Fixture("round-silo-apibay-exact.json"),
            new("https://apibay.org/q.php?q=Silo.S02E01.1080p.WEB.H264-SuccessfulCrab&cat="));

        SourceRow tgx = rows.Single(row => row.Title == "Silo.S02E01.1080p.WEB.H264-SuccessfulCrab[TGx]");

        Assert.Equal(new DateTimeOffset(2024, 11, 15, 2, 23, 57, TimeSpan.Zero), tgx.Published);
    }

    /// <remarks>EZTV's endpoint writes it as a number: <c>"date_released_unix":1786753216</c>.</remarks>
    [Fact]
    public void TheEztvEndpointGivesTheMomentAnEpisodeWasReleased()
    {
        IReadOnlyList<SourceRow> rows = new EztvApiReader().Read(
            Capture.Fixture("eztv-latest.json"),
            new("https://eztv.re/api/get-torrents?limit=100"));

        Assert.Equal("The.Young.and.the.Restless.S53E217.XviD-AFG[EZTVx.to].avi", rows[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 8, 15, 0, 20, 16, TimeSpan.Zero), rows[0].Published);
    }

    /// <remarks>Nyaa's feed dates each item: <c>Sat, 02 Mar 2024 02:09:32 -0000</c>.</remarks>
    [Fact]
    public void NyaaGivesTheMomentAnItemWasPublished()
    {
        IReadOnlyList<SourceRow> rows = new TorrentRssReader().Read(
            Capture.Fixture("nyaa.xml"),
            new("https://nyaa.si/?page=rss&q=Frieren+S01E13"));

        Assert.Equal("C5B9337296E01CA2C1CC6D7938451F49C47011F2", rows[0].InfoHash);
        Assert.Equal(new DateTimeOffset(2024, 3, 2, 2, 9, 32, TimeSpan.Zero), rows[0].Published);
    }

    /// <remarks>
    /// TorrentBay prints an age and puts the date in its title: <c>&lt;span title="15 November 2024"&gt;1 year
    /// ago&lt;/span&gt;</c>. The day, with no hour.
    /// </remarks>
    [Fact]
    public void TorrentBayGivesTheDayBehindTheAgeItPrints()
    {
        IReadOnlyList<SourceRow> rows = new TorrentBayReader().Read(
            Capture.Fixture("round-silo-torrentbay-exact.html"),
            new("https://extranet.torrentbay.st/browse/?q=Silo.S02E01.1080p.WEB.H264-SuccessfulCrab"));

        Assert.Equal("Silo.S02E01.1080p.WEB.H264-SuccessfulCrab[TGx]", rows[0].Title);
        Assert.Equal(new DateTimeOffset(2024, 11, 15, 0, 0, 0, TimeSpan.Zero), rows[0].Published);
    }

    /// <remarks>
    /// Torrentz2 puts the moment in the title of the age, and it has written it two ways: the browser's own
    /// rendering, <c>Mon Nov 18 2024 21:09:41 GMT+0000 (Coordinated Universal Time)</c>, on the capture of
    /// 15 September 2026, and <c>2026-08-07T18:02:19.368Z</c> on the capture of 15 August 2026.
    /// </remarks>
    [Fact]
    public void Torrentz2GivesTheMomentBehindTheAgeItPrintsInEitherOfItsForms()
    {
        IReadOnlyList<SourceRow> rendered = new Torrentz2Reader().Read(
            Capture.Fixture("round-silo-torrentz2-exact.html"),
            new("https://torrentz2.nz/search?q=Silo.S02E01.1080p.WEB.H264-SuccessfulCrab"));

        Assert.Equal("Silo.S02E01.1080p.WEB.H264-SuccessfulCrab[TGx]", rendered[0].Title);
        Assert.Equal(new DateTimeOffset(2024, 11, 18, 21, 9, 41, TimeSpan.Zero), rendered[0].Published);

        IReadOnlyList<SourceRow> written = new Torrentz2Reader().Read(
            Capture.Fixture("torrentz2.html"),
            new("https://torrentz2.nz/search?q=Silo+S03E06"));

        Assert.Equal("silo.s03e06.1080p.web.h264-cakes[EZTVx.to].mkv", written[0].Title);
        Assert.Equal(new DateTimeOffset(2026, 8, 7, 18, 2, 19, 368, TimeSpan.Zero), written[0].Published);
    }

    /// <remarks>
    /// 1337x writes a date with its year for an upload of an earlier year, <c>Nov. 15th '24</c>, and one of
    /// this year without it, <c>11pm Aug. 11th</c>. Only the first says which year it is; the second is left
    /// undated rather than given a year the page never wrote.
    /// </remarks>
    [Fact]
    public void X1337GivesTheDayOnlyWhereItWritesTheYear()
    {
        IReadOnlyList<SourceRow> dated = new X1337Reader().Read(
            Capture.Fixture("round-silo-1337x-exact.html"),
            new("https://www.1337x.to/sort-category-search/Silo.S02E01.1080p.WEB.H264-SuccessfulCrab/TV/time/desc/1/"));

        SourceRow tgx = Assert.Single(dated);

        Assert.Equal(IndexerSites.X1337Detail, tgx.DetailUrl?.ToString());
        Assert.Equal(new DateTimeOffset(2024, 11, 15, 0, 0, 0, TimeSpan.Zero), tgx.Published);

        IReadOnlyList<SourceRow> thisYear = new X1337Reader().Read(
            Capture.Fixture("1337x.html"),
            new("https://www.1337x.to/sort-category-search/Silo+S03E06/TV/time/desc/1/"));

        Assert.NotEmpty(thisYear);
        Assert.All(thisYear, row => Assert.Null(row.Published));
    }
}
