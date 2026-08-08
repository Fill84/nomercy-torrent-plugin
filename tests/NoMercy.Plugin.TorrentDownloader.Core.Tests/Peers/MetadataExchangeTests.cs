// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Security.Cryptography;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers;

public class MetadataExchangeTests
{
    /// <summary>The info dictionary of a real torrent, which is what BEP 9 transfers.</summary>
    private static byte[] InfoDictionary(int fillerBytes = 0)
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("season")
            .WithPieceLength(16384)
            .WithFile("season/e01.mkv", new byte[16384 + fillerBytes])
            .WithFile("season/e02.mkv", new byte[16384]);

        BDictionary root = (BDictionary)BencodeReader.Parse(builder.Build());
        return BencodeWriter.Write(root.Entries["info"]);
    }

    [Fact]
    public void ExtensionHandshake_AdvertisesUtMetadataAndOurSize()
    {
        byte[] payload = ExtensionHandshake.Write(metadataSize: 1234);

        ExtensionHandshake parsed = ExtensionHandshake.Parse(payload);

        parsed.UtMetadataId.Should().NotBeNull();
        parsed.MetadataSize.Should().Be(1234);
    }

    [Fact]
    public void ExtensionHandshake_OmitsTheSizeWhenWeDoNotHaveTheMetadataYet()
    {
        ExtensionHandshake parsed = ExtensionHandshake.Parse(ExtensionHandshake.Write(metadataSize: null));

        parsed.MetadataSize.Should().BeNull();
        parsed.UtMetadataId.Should().NotBeNull();
    }

    [Fact]
    public void ExtensionHandshake_ReadsThePeersOwnChoiceOfIdentifier()
    {
        // Each side picks its own number for an extension and the other must use it.
        // Assuming our own id works with most clients and fails with the rest.
        byte[] theirs = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["m"] = new BDictionary(new Dictionary<string, BValue> { ["ut_metadata"] = new BInteger(7) }),
            ["metadata_size"] = new BInteger(999),
        }));

        ExtensionHandshake parsed = ExtensionHandshake.Parse(theirs);

        parsed.UtMetadataId.Should().Be(7);
        parsed.MetadataSize.Should().Be(999);
    }

    [Fact]
    public void ExtensionHandshake_SaysNothingWhenThePeerDoesNotSupportUtMetadata()
    {
        byte[] theirs = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["m"] = new BDictionary(new Dictionary<string, BValue> { ["ut_pex"] = new BInteger(2) }),
        }));

        ExtensionHandshake.Parse(theirs).UtMetadataId.Should().BeNull();
    }

    [Fact]
    public void MetadataMessage_RoundTripsARequest()
    {
        byte[] payload = MetadataMessage.WriteRequest(piece: 3);

        MetadataMessage parsed = MetadataMessage.Parse(payload);

        parsed.Type.Should().Be(MetadataMessageType.Request);
        parsed.Piece.Should().Be(3);
    }

    [Fact]
    public void MetadataMessage_RoundTripsDataWithItsPayloadAfterTheDictionary()
    {
        byte[] block = [1, 2, 3, 4, 5];

        MetadataMessage parsed = MetadataMessage.Parse(MetadataMessage.WriteData(piece: 1, totalSize: 40, block));

        parsed.Type.Should().Be(MetadataMessageType.Data);
        parsed.Piece.Should().Be(1);
        parsed.TotalSize.Should().Be(40);
        parsed.Data.Should().Equal(block);
    }

    [Fact]
    public void MetadataMessage_ReadsAReject()
    {
        MetadataMessage parsed = MetadataMessage.Parse(MetadataMessage.WriteReject(piece: 2));

        parsed.Type.Should().Be(MetadataMessageType.Reject);
        parsed.Piece.Should().Be(2);
    }

    [Fact]
    public void MetadataDownload_AsksForEveryPieceItNeeds()
    {
        byte[] info = InfoDictionary(fillerBytes: 40000);
        MetadataDownload download = new(SHA1.HashData(info), info.Length);

        // Metadata travels in 16 KiB pieces like anything else on this wire.
        download.PieceCount.Should().Be((info.Length + 16383) / 16384);
        download.MissingPieces().Should().Equal(Enumerable.Range(0, download.PieceCount));
    }

    [Fact]
    public void MetadataDownload_RebuildsTheTorrentAndItHashesToTheMagnetsInfoHash()
    {
        byte[] info = InfoDictionary(fillerBytes: 40000);
        byte[] infoHash = SHA1.HashData(info);
        MetadataDownload download = new(infoHash, info.Length);

        for (int piece = 0; piece < download.PieceCount; piece++)
        {
            int offset = piece * 16384;
            int length = Math.Min(16384, info.Length - offset);
            download.Accept(piece, info.AsSpan(offset, length).ToArray()).Should().BeTrue();
        }

        download.IsComplete.Should().BeTrue();

        TorrentMetadata metadata = download.Build(["http://tracker.test/announce"]);

        // The whole point: metadata fetched from strangers must hash to the info hash
        // the magnet named, or it is somebody else's torrent.
        metadata.InfoHash.Should().Equal(infoHash);
        metadata.Name.Should().Be("season");
        metadata.Files.Should().HaveCount(2);
        metadata.Trackers.Should().Equal("http://tracker.test/announce");
    }

    [Fact]
    public void MetadataDownload_RefusesToBuildFromBytesThatHashToSomethingElse()
    {
        byte[] info = InfoDictionary();
        byte[] wrongHash = Enumerable.Repeat((byte)0xAB, 20).ToArray();
        MetadataDownload download = new(wrongHash, info.Length);

        download.Accept(0, info).Should().BeTrue();
        download.IsComplete.Should().BeTrue();

        Action build = () => download.Build([]);

        build.Should().Throw<MetadataException>().WithMessage("*hash*");
    }

    [Fact]
    public void MetadataDownload_IgnoresAPieceOfTheWrongLength()
    {
        byte[] info = InfoDictionary(fillerBytes: 40000);
        MetadataDownload download = new(SHA1.HashData(info), info.Length);

        download.Accept(0, new byte[100]).Should().BeFalse();
        download.MissingPieces().Should().Contain(0);
    }

    [Fact]
    public void MetadataDownload_IgnoresAPieceIndexThatDoesNotExist()
    {
        byte[] info = InfoDictionary();
        MetadataDownload download = new(SHA1.HashData(info), info.Length);

        download.Accept(99, info).Should().BeFalse();
        download.Accept(-1, info).Should().BeFalse();
    }

    [Fact]
    public void MetadataDownload_RefusesAnImplausibleSizeUpFront()
    {
        // A peer naming a 500 MB info dictionary is trying to make us allocate on
        // command. No real torrent's metadata is anywhere near that.
        Action absurd = () => _ = new MetadataDownload(new byte[20], 500 * 1024 * 1024);

        absurd.Should().Throw<MetadataException>();
    }
}
