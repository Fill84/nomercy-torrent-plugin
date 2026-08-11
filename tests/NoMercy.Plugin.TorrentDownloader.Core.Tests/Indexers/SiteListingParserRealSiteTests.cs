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
        SiteListingParser.Parse(Fixtures.Text("limetorrents-search.html"));

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

    [Fact]
    public void Parse_CountsEachReleaseOnce()
    {
        Rows().Select(row => row.InfoHash).Should().OnlyHaveUniqueItems();
    }
}
