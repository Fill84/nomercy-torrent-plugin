// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

public class MagnetLinkTests
{
    private const string Hex = "123456789abcdef00020417e2d5f2e7aff010203";

    private static readonly byte[] Expected =
        [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x00, 0x20, 0x41, 0x7E, 0x2D, 0x5F, 0x2E, 0x7A, 0xFF, 0x01, 0x02, 0x03];

    [Fact]
    public void Parse_ReadsAHexInfoHash()
    {
        MagnetLink magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}");

        magnet.InfoHash.Should().Equal(Expected);
    }

    [Fact]
    public void Parse_ReadsABase32InfoHash()
    {
        // Older sites still hand out the 32-character base32 form. It is the same
        // twenty bytes, and a client that only reads hex silently refuses half the web.
        MagnetLink magnet = MagnetLink.Parse("magnet:?xt=urn:btih:CI2FM6E2XTPPAABAIF7C2XZOPL7QCAQD");

        magnet.InfoHash.Should().Equal(Expected);
    }

    [Fact]
    public void Parse_IsIndifferentToCase()
    {
        MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex.ToUpperInvariant()}").InfoHash.Should().Equal(Expected);
        MagnetLink.Parse($"MAGNET:?XT=urn:btih:{Hex}").InfoHash.Should().Equal(Expected);
    }

    [Fact]
    public void Parse_ReadsTheDisplayName()
    {
        MagnetLink magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&dn=Some.Show.S01E01.1080p");

        magnet.DisplayName.Should().Be("Some.Show.S01E01.1080p");
    }

    [Fact]
    public void Parse_DecodesAPercentEncodedDisplayName()
    {
        MagnetLink magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&dn=Some%20Show%20%5B1080p%5D");

        magnet.DisplayName.Should().Be("Some Show [1080p]");
    }

    [Fact]
    public void Parse_CollectsEveryTracker()
    {
        MagnetLink magnet = MagnetLink.Parse(
            $"magnet:?xt=urn:btih:{Hex}" +
            "&tr=udp%3A%2F%2Ftracker.one%3A1337%2Fannounce" +
            "&tr=http%3A%2F%2Ftracker.two%2Fannounce");

        magnet.Trackers.Should().Equal(
            "udp://tracker.one:1337/announce",
            "http://tracker.two/announce");
    }

    [Fact]
    public void Parse_KeepsNoTrackersWhenTheLinkNamesNone()
    {
        // A trackerless magnet is normal - DHT is meant to find the swarm.
        MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}").Trackers.Should().BeEmpty();
    }

    [Fact]
    public void Parse_IgnoresParametersItDoesNotUnderstand()
    {
        MagnetLink magnet = MagnetLink.Parse($"magnet:?xt=urn:btih:{Hex}&xl=1234&ws=http://example.test/&so=0-3");

        magnet.InfoHash.Should().Equal(Expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test/file.torrent")]
    [InlineData("magnet:?dn=no-hash-here")]
    [InlineData("magnet:?xt=urn:sha1:123456789abcdef00020417e2d5f2e7aff010203")]
    [InlineData("magnet:?xt=urn:btih:tooshort")]
    [InlineData("magnet:?xt=urn:btih:zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz")]
    public void Parse_RejectsWhatIsNotAUsableMagnet(string link)
    {
        Action parse = () => MagnetLink.Parse(link);

        parse.Should().Throw<MetadataException>();
    }

    [Fact]
    public void TryParse_AnswersFalseInsteadOfThrowing()
    {
        MagnetLink.TryParse("not a magnet", out MagnetLink? magnet).Should().BeFalse();
        magnet.Should().BeNull();

        MagnetLink.TryParse($"magnet:?xt=urn:btih:{Hex}", out MagnetLink? good).Should().BeTrue();
        good!.InfoHash.Should().Equal(Expected);
    }
}
