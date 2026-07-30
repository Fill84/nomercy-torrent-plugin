// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
//
// NoMercy MediaServer Automated Torrent Plugin 
// Created by Phillippe Pelzer https://github.com/Fill84
// -----------------------------------------------------------------------------

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class RssFeedParserTests
{
    private static IReadOnlyList<RssItem> RealFeed() =>
        RssFeedParser.Parse(Fixtures.Text("scnsrc-feed.xml"));

    [Fact]
    public void Parse_ReadsEveryItemFromTheRealCapture()
    {
        RealFeed().Should().HaveCount(40);
    }

    [Fact]
    public void Parse_ReadsTitleLinkGuidAndPublishedDate()
    {
        RssItem first = RealFeed()[0];

        first.Title.Should().Be(
            "The Kelly Clarkson Show 2026 07 22 Guest Host Andy Cohen 1080p WEB h264-DiRT"
        );
        first.Link.Should().StartWith("https://www.scnsrc.me/");
        first.Guid.Should().Be("https://www.scnsrc.me/?p=541034");
        first.Published.Should().Be(new DateTimeOffset(2026, 7, 24, 20, 5, 42, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_ReadsAllCategoriesNotJustTheFirst()
    {
        RssItem multiCategory = RealFeed()
            .Single(item => item.Title == "Her Private Hell 2026 720p CAM H264-CinemaCity");

        multiCategory.Categories.Should().BeEquivalentTo(["Cam", "Movies", "P2P"]);
    }

    [Fact]
    public void Parse_LeavesEnclosureEmptyForADiscoveryOnlyFeed()
    {
        RealFeed().Should().OnlyContain(item => item.EnclosureUrl == null);
    }

    [Fact]
    public void Parse_ReadsAnEnclosureWhenTheFeedOffersOne()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <link>https://tracker.example/t/1</link>
                <enclosure url="https://tracker.example/t/1.torrent"
                           length="1503238553"
                           type="application/x-bittorrent" />
              </item>
            </channel></rss>
            """;

        RssItem item = RssFeedParser.Parse(xml).Single();

        item.EnclosureUrl.Should().Be("https://tracker.example/t/1.torrent");
        item.EnclosureLength.Should().Be(1503238553L);
        item.EnclosureType.Should().Be("application/x-bittorrent");
    }

    [Fact]
    public void Parse_SkipsAnItemWithNoTitle()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item><link>https://x/1</link></item>
              <item><title>Silo S03E04 1080p</title></item>
            </channel></rss>
            """;

        RssFeedParser.Parse(xml).Should().ContainSingle();
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnMalformedXml()
    {
        Action act = () => RssFeedParser.Parse("<rss><channel><item>");

        act.Should().Throw<IndexerException>().WithMessage("*feed*");
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnAnEmptyBody()
    {
        Action act = () => RssFeedParser.Parse("");

        act.Should().Throw<IndexerException>();
    }

    [Fact]
    public void Parse_LeavesPublishedNullWhenTheDateIsUnparseable()
    {
        string xml = """
            <rss version="2.0"><channel>
              <item><title>Silo S03E04</title><pubDate>not a date</pubDate></item>
            </channel></rss>
            """;

        RssFeedParser.Parse(xml).Single().Published.Should().BeNull();
    }
}
