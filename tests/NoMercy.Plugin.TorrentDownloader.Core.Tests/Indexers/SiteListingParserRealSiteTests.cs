// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

/// <summary>
/// A real search page from a site the owner actually configured, captured from their own
/// server on 11 August 2026.
///
/// <para>
/// The parser was written on one assumption - "torrent listings agree on one thing even when
/// they agree on nothing else: the magnet link is in the page" - and this page has no magnet
/// anywhere in it. Every search against this site therefore found nothing, every day, while
/// the site answered 200 with four usable releases for the episode being asked about. Zero
/// grabs in a fortnight, and nothing in any log to say why.
/// </para>
///
/// <para>
/// What the page does carry is a link to the torrent file whose name is the infohash:
/// <c>itorrents.net/torrent/2124…C271.torrent?title=Silo-S03E04-1080p-HEVC-x265-MeGusta</c>.
/// A magnet can be built from that without fetching anything - which also matters because
/// that file lives on a third host the owner never granted.
/// </para>
/// </summary>
public class SiteListingParserRealSiteTests
{
    private static IReadOnlyList<SiteRow> Rows() =>
        SiteListingParser.Parse(Fixtures.Text("limetorrents-search.html"), []);

    [Fact]
    public void Parse_FindsTheReleasesOnAPageWithNoMagnetInIt()
    {
        Rows().Should().NotBeEmpty("the page holds four releases for the episode that was searched for");
    }

    [Fact]
    public void Parse_ReadsTheReleaseNameAsTheSiteWroteIt()
    {
        Rows().Select(row => row.Title).Should().Contain("Silo S03E04 1080p HEVC x265-MeGusta");
    }

    /// <summary>
    /// Built, not fetched. The torrent file is on itorrents.net - a host the owner never
    /// granted and never should have to - and everything needed to identify the torrent is
    /// already in the URL.
    /// </summary>
    [Fact]
    public void Parse_BuildsAMagnetFromTheHashInTheTorrentUrl()
    {
        SiteRow row = Rows().Single(row => row.Title == "Silo S03E04 1080p HEVC x265-MeGusta");

        row.InfoHash.Should().Be("212488687f9cbdfd74cedba7a43eeb91fe82c271");
        row.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:212488687f9cbdfd74cedba7a43eeb91fe82c271");
        row.MagnetUri.Should().Contain("dn=");
    }

    /// <summary>
    /// The count is in a table cell, <c>class="tdseed"&gt;3,038</c>, with a thousands
    /// separator and no word "seeders" anywhere near it. Read as zero, the owner's minimum
    /// of two refuses every row on the site - so a parser that found the release and missed
    /// this would still never download anything.
    /// </summary>
    [Fact]
    public void Parse_ReadsSeedersOutOfTheTableCellThatHoldsThem()
    {
        SiteRow row = Rows().Single(row => row.Title == "Silo S03E04 1080p HEVC x265-MeGusta");

        row.Seeders.Should().Be(3038);
    }

    /// <summary>
    /// The page opens with three "Sponsored Links" rows carrying the same episode name and a
    /// link to somebody's affiliate search. They have no torrent behind them, so a parser
    /// keyed on the release link never sees them - which is the point of keying on it.
    /// </summary>
    [Fact]
    public void Parse_IgnoresTheSponsoredRowsAtTheTopOfThePage()
    {
        Rows().Should().OnlyContain(row => row.MagnetUri.StartsWith("magnet:?xt=urn:btih:"));
        Rows().Should().NotContain(row => row.Title.Contains("Download", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A magnet built from a hash names a torrent and no way to find anybody who has it.
    /// DHT alone answered nobody on a real swarm: five minutes of asking, then a
    /// MetadataException. The trackers are the owner's setting, so a site that publishes
    /// none still resolves.
    /// </summary>
    [Fact]
    public void Parse_PutsTheConfiguredTrackersOnAMagnetItBuilt()
    {
        IReadOnlyList<SiteRow> rows = SiteListingParser.Parse(
            Fixtures.Text("limetorrents-search.html"),
            ["udp://tracker.example:1337/announce", "udp://other.example:80/announce"]);

        string magnet = rows.First().MagnetUri;

        magnet.Should().Contain($"tr={Uri.EscapeDataString("udp://tracker.example:1337/announce")}");
        magnet.Should().Contain($"tr={Uri.EscapeDataString("udp://other.example:80/announce")}");
    }

    /// <summary>
    /// A site that publishes its own magnet already names the swarm its users are in.
    /// Appending to that is guesswork on top of fact.
    /// </summary>
    [Fact]
    public void Parse_LeavesAMagnetTheSitePublishedExactlyAsItFoundIt()
    {
        const string html =
            """<a href="magnet:?xt=urn:btih:0123456789abcdef0123456789abcdef01234567&dn=A.Show.S01E01">x</a>""";

        SiteListingParser.Parse(html, ["udp://tracker.example:1337/announce"])
            .Should().ContainSingle().Which.MagnetUri.Should().NotContain("tracker.example");
    }

    [Fact]
    public void Parse_CountsEachReleaseOnce()
    {
        Rows().Select(row => row.InfoHash).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// TorrentBay's real search page, captured through the plugin's own browser after it
    /// cleared the Cloudflare challenge.
    ///
    /// <para>
    /// This site keeps no magnet on its listing - fifty rows, zero magnets, one .torrent
    /// link in the whole document - so the parser read nothing from it for as long as it
    /// only understood magnets and hashed links. The row carries a title, a slug and a
    /// seeder count, and the magnet lives behind the slug.
    /// </para>
    /// </summary>
    [Fact]
    public void Parse_ReadsARowThatOnlyLinksToItsDetailPage()
    {
        IReadOnlyList<SiteRow> rows = SiteListingParser.Parse(Fixtures.Text("torrentbay-search.html"), []);

        rows.Should().HaveCountGreaterThan(40);

        SiteRow first = rows[0];

        first.Title.Should().Be("Silo S03E06 1080p WEB H264-CAKES EZTV");
        first.DetailUrl.Should().Be("/silo-s03e06-1080p-web-h264-cakes-eztv-21152668/");
        first.MagnetUri.Should().BeNull("this listing has none - the magnet is behind the slug");
    }

    /// <summary>
    /// The seeder count, which is the score.
    ///
    /// <para>
    /// Written as a label in one tag and a number in the next, and sitting seven thousand
    /// characters past the title because each row carries an inline SVG badge. Read within a
    /// window measured in hundreds, every row scored zero - and a profile with a minimum
    /// seeder count then refuses every row of a site that answered perfectly.
    /// </para>
    /// </summary>
    [Fact]
    public void Parse_ReadsTheSeederCountOutOfARowThatIsThousandsOfCharactersLong()
    {
        IReadOnlyList<SiteRow> rows = SiteListingParser.Parse(Fixtures.Text("torrentbay-search.html"), []);

        rows[0].Seeders.Should().Be(4779);
        rows.Take(6).Should().OnlyContain(row => row.Seeders > 0);
    }

    /// <summary>
    /// The title comes from the tooltip, not the link text: the link text wraps the search
    /// term in a span, and stripping tags glues the tokens into "SiloS03E06".
    /// </summary>
    [Fact]
    public void Parse_DoesNotGlueTheSearchTermToTheRestOfTheTitle()
    {
        SiteListingParser.Parse(Fixtures.Text("torrentbay-search.html"), [])
            .Should().OnlyContain(row => !row.Title.Contains("SiloS", StringComparison.Ordinal));
    }
}
