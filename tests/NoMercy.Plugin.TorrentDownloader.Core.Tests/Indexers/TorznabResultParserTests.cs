// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Indexers.Parsing;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Indexers;

public class TorznabResultParserTests
{
    private const string Response = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
          <channel>
            <item>
              <title>Silo S03E04 1080p WEB H264-CAKES</title>
              <guid>https://indexer.example/details/1</guid>
              <comments>https://indexer.example/details/1</comments>
              <pubDate>Fri, 24 Jul 2026 20:05:42 +0000</pubDate>
              <size>1503238553</size>
              <link>https://indexer.example/download/1.torrent</link>
              <torznab:attr name="seeders" value="42" />
              <torznab:attr name="peers" value="50" />
              <torznab:attr name="infohash" value="ABCDEF0123456789ABCDEF0123456789ABCDEF01" />
            </item>
            <item>
              <title>Silo S03E05 720p WEB H264-CAKES</title>
              <guid>https://indexer.example/details/2</guid>
              <size>800000000</size>
              <link>magnet:?xt=urn:btih:1111111111111111111111111111111111111111&amp;dn=Silo</link>
              <torznab:attr name="seeders" value="7" />
              <torznab:attr name="peers" value="9" />
            </item>
          </channel>
        </rss>
        """;

    private static IReadOnlyList<ReleaseInfo> Parsed() =>
        TorznabResultParser.Parse(Response, "prowlarr", 9);

    [Fact]
    public void Parse_ReadsEveryItem()
    {
        Parsed().Should().HaveCount(2);
    }

    [Fact]
    public void Parse_ReadsSeedersFromTheNamespacedAttribute()
    {
        Parsed()[0].Seeders.Should().Be(42);
    }

    [Fact]
    public void Parse_DerivesLeechersBySubtractingSeedersFromPeers()
    {
        Parsed()[0].Leechers.Should().Be(8);
    }

    [Fact]
    public void Parse_LowercasesTheInfoHash()
    {
        Parsed()[0].InfoHash.Should().Be("abcdef0123456789abcdef0123456789abcdef01");
    }

    [Fact]
    public void Parse_ReadsSizeAndPublishedDate()
    {
        ReleaseInfo first = Parsed()[0];

        first.SizeBytes.Should().Be(1503238553L);
        first.PublishedAt.Should().Be(new DateTimeOffset(2026, 7, 24, 20, 5, 42, TimeSpan.Zero));
    }

    [Fact]
    public void Parse_TreatsAnHttpLinkAsADownloadUrl()
    {
        ReleaseInfo first = Parsed()[0];

        first.DownloadUrl.Should().Be("https://indexer.example/download/1.torrent");
        first.MagnetUri.Should().BeNull();
    }

    [Fact]
    public void Parse_TreatsAMagnetLinkAsAMagnetAndRecoversItsInfoHash()
    {
        ReleaseInfo second = Parsed()[1];

        second.MagnetUri.Should().StartWith("magnet:?xt=urn:btih:");
        second.DownloadUrl.Should().BeNull();
        second.InfoHash.Should().Be("1111111111111111111111111111111111111111");
    }

    [Fact]
    public void Parse_StampsTheIndexerNameAndPriority()
    {
        Parsed().Should().OnlyContain(release => release.IndexerName == "prowlarr");
        Parsed().Should().OnlyContain(release => release.IndexerPriority == 9);
    }

    [Fact]
    public void Parse_ReadsSizeFromTheAttributeWhenThereIsNoSizeElement()
    {
        string attrSize = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <torznab:attr name="size" value="1503238553" />
              </item></channel>
            </rss>
            """;

        TorznabResultParser.Parse(attrSize, "x", 0).Single().SizeBytes.Should().Be(1503238553L);
    }

    [Fact]
    public void Parse_PrefersTheSizeElementWhenBothArePresent()
    {
        string both = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item>
                <title>Silo S03E04 1080p WEB H264-CAKES</title>
                <size>111</size>
                <torznab:attr name="size" value="222" />
              </item></channel>
            </rss>
            """;

        TorznabResultParser.Parse(both, "x", 0).Single().SizeBytes.Should().Be(111L);
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnAnErrorDocument()
    {
        string error = """
            <?xml version="1.0" encoding="UTF-8"?>
            <error code="100" description="Incorrect user credentials" />
            """;

        Action act = () => TorznabResultParser.Parse(error, "prowlarr", 9);

        act.Should()
            .Throw<IndexerException>()
            .WithMessage("*Incorrect user credentials*");
    }

    [Fact]
    public void Parse_ThrowsIndexerExceptionOnMalformedXml()
    {
        Action act = () => TorznabResultParser.Parse("<rss>", "prowlarr", 9);

        act.Should().Throw<IndexerException>();
    }

    [Fact]
    public void Parse_DefaultsMissingAttributesToZeroRatherThanThrowing()
    {
        string sparse = """
            <rss version="2.0" xmlns:torznab="http://torznab.com/schemas/2015/feed">
              <channel><item><title>Silo S03E04 1080p</title></item></channel>
            </rss>
            """;

        ReleaseInfo release = TorznabResultParser.Parse(sparse, "x", 0).Single();

        release.Seeders.Should().Be(0);
        release.Leechers.Should().Be(0);
        release.SizeBytes.Should().Be(0);
    }
}
