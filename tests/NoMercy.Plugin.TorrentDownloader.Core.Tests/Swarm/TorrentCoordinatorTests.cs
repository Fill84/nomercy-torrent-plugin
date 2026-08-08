// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

public class TorrentCoordinatorTests
{
    private static readonly PeerKey Alice = new(1);
    private static readonly PeerKey Bob = new(2);
    private static readonly PeerKey Carol = new(3);

    /// <summary>Four pieces of four bytes: "aaaa" "bbbb" "cccc" "dddd".</summary>
    private static TorrentBuilder FourPieces() => new TorrentBuilder()
        .WithName("four")
        .WithPieceLength(4)
        .WithFile("four", "aaaabbbbccccdddd");

    private static TorrentMetadata Metadata() => MetadataParser.FromTorrentFile(FourPieces().Build());

    private static byte[] Piece(int index) => Encoding.ASCII.GetBytes(new string((char)('a' + index), 4));

    private static TorrentCoordinator Fresh(TorrentMetadata? metadata = null, SwarmPolicy? policy = null)
    {
        TorrentMetadata resolved = metadata ?? Metadata();
        return new TorrentCoordinator(resolved, new Bitfield(resolved.PieceCount), policy ?? SwarmPolicy.Default);
    }

    private static Bitfield Holding(int pieceCount, params int[] indices)
    {
        Bitfield field = new(pieceCount);

        foreach (int index in indices)
            field[index] = true;

        return field;
    }

    private static List<Request> RequestsIn(IEnumerable<CoordinatorAction> actions) =>
        [.. actions.OfType<SendMessage>().Select(action => action.Message).OfType<Request>()];

    [Fact]
    public void PeerAnnouncedBitfield_SaysInterestedWhenThePeerHasSomethingWeNeed()
    {
        TorrentCoordinator coordinator = Fresh();

        IReadOnlyList<CoordinatorAction> actions = coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1));

        actions.OfType<SendMessage>().Select(action => action.Message).Should().ContainSingle()
            .Which.Should().Be(new Interested());
    }

    [Fact]
    public void PeerAnnouncedBitfield_StaysQuietWhenThePeerHasNothingWeNeed()
    {
        TorrentCoordinator coordinator = Fresh();

        IReadOnlyList<CoordinatorAction> actions = coordinator.PeerAnnouncedBitfield(Alice, new Bitfield(4));

        actions.Should().BeEmpty();
    }

    [Fact]
    public void PeerUnchoked_AsksAChokedPeerForNothing()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1, 2, 3));

        // No unchoke has arrived, so nothing may be requested yet.
        coordinator.PeerAnnouncedHave(Alice, 0).OfType<SendMessage>()
            .Select(action => action.Message).OfType<Request>().Should().BeEmpty();
    }

    [Fact]
    public void PeerUnchoked_RequestsTheRarestPieceFirst()
    {
        TorrentCoordinator coordinator = Fresh();

        // Piece 3 is held by one peer; pieces 0 and 1 by three. Rarest first means
        // the scarce piece is fetched while its only source is still here.
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1, 2, 3));
        coordinator.PeerAnnouncedBitfield(Bob, Holding(4, 0, 1, 2));
        coordinator.PeerAnnouncedBitfield(Carol, Holding(4, 0, 1, 2));

        List<Request> requests = RequestsIn(coordinator.PeerUnchoked(Alice));

        requests.Should().NotBeEmpty();
        requests[0].PieceIndex.Should().Be(3);
    }

    [Fact]
    public void PeerUnchoked_NeverAsksForAPieceWeAlreadyHold()
    {
        TorrentMetadata metadata = Metadata();
        TorrentCoordinator coordinator = new(metadata, Holding(4, 0, 1, 2), SwarmPolicy.Default);

        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1, 2, 3));
        List<Request> requests = RequestsIn(coordinator.PeerUnchoked(Alice));

        requests.Should().OnlyContain(request => request.PieceIndex == 3);
    }

    [Fact]
    public void PeerUnchoked_NeverAsksForAPieceThePeerDoesNotHold()
    {
        TorrentCoordinator coordinator = Fresh();

        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 2));
        List<Request> requests = RequestsIn(coordinator.PeerUnchoked(Alice));

        requests.Should().OnlyContain(request => request.PieceIndex == 2);
    }

    [Fact]
    public void BlockReceived_CompletesAPieceAndRecordsIt()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));
        coordinator.PeerUnchoked(Alice);

        IReadOnlyList<CoordinatorAction> actions = coordinator.BlockReceived(Alice, 0, 0, Piece(0));

        actions.OfType<PieceReady>().Should().ContainSingle()
            .Which.Should().Match<PieceReady>(ready => ready.PieceIndex == 0 && ready.Data.SequenceEqual(Piece(0)));
        coordinator.Have[0].Should().BeTrue();
    }

    [Fact]
    public void BlockReceived_AnnouncesAFinishedPieceToEveryPeer()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));
        coordinator.PeerAnnouncedBitfield(Bob, Holding(4, 0, 1));
        coordinator.PeerUnchoked(Alice);

        IReadOnlyList<CoordinatorAction> actions = coordinator.BlockReceived(Alice, 0, 0, Piece(0));

        actions.OfType<SendMessage>()
            .Where(action => action.Message is Have)
            .Select(action => action.Peer)
            .Should().BeEquivalentTo([Alice, Bob]);
    }

    [Fact]
    public void BlockReceived_RejectsAPieceWhoseHashIsWrongAndDoesNotRecordIt()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));
        coordinator.PeerUnchoked(Alice);

        IReadOnlyList<CoordinatorAction> actions = coordinator.BlockReceived(Alice, 0, 0, "XXXX"u8.ToArray());

        actions.OfType<PieceRejected>().Should().ContainSingle().Which.PieceIndex.Should().Be(0);
        actions.OfType<PieceReady>().Should().BeEmpty();
        coordinator.Have[0].Should().BeFalse();
    }

    [Fact]
    public void BlockReceived_BansAPeerOnItsThirdBadPieceAndNotItsSecond()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1, 2, 3));
        coordinator.PeerUnchoked(Alice);

        coordinator.BlockReceived(Alice, 0, 0, "XXXX"u8.ToArray()).OfType<BanPeer>().Should().BeEmpty();
        coordinator.BlockReceived(Alice, 0, 0, "XXXX"u8.ToArray()).OfType<BanPeer>().Should().BeEmpty();

        // One bad piece is luck. Three is a pattern.
        coordinator.BlockReceived(Alice, 0, 0, "XXXX"u8.ToArray())
            .OfType<BanPeer>().Should().ContainSingle().Which.Peer.Should().Be(Alice);
    }

    [Fact]
    public void BlockReceived_IgnoresABlockNobodyAskedFor()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));

        // No unchoke, so no request went out. An unsolicited block is not evidence
        // of anything and must not be able to complete a piece.
        IReadOnlyList<CoordinatorAction> actions = coordinator.BlockReceived(Alice, 0, 0, Piece(0));

        actions.Should().BeEmpty();
        coordinator.Have[0].Should().BeFalse();
    }

    [Fact]
    public void BlockReceived_AssemblesAPieceFromSeveralBlocks()
    {
        TorrentBuilder builder = new TorrentBuilder()
            .WithName("big")
            .WithPieceLength(32768)
            .WithFile("big", new byte[40000]);
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        TorrentCoordinator coordinator = Fresh(metadata);

        coordinator.PeerAnnouncedBitfield(Alice, Holding(metadata.PieceCount, 0));
        List<Request> requests = RequestsIn(coordinator.PeerUnchoked(Alice));

        // 32 KiB is two 16 KiB blocks, so the piece cannot arrive in one message.
        requests.Where(request => request.PieceIndex == 0).Should().HaveCount(2);

        coordinator.BlockReceived(Alice, 0, 0, new byte[16384]).OfType<PieceReady>().Should().BeEmpty();
        coordinator.BlockReceived(Alice, 0, 16384, new byte[16384])
            .OfType<PieceReady>().Should().ContainSingle();
    }

    [Fact]
    public void PeerDisconnected_ReleasesItsOutstandingBlocksForSomebodyElse()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));
        coordinator.PeerAnnouncedBitfield(Bob, Holding(4, 0));
        coordinator.PeerUnchoked(Alice);

        coordinator.PeerDisconnected(Alice);

        // Alice was asked for piece 0 and left. If her request is not released, the
        // piece is never asked for again and the download stalls one piece short.
        RequestsIn(coordinator.PeerUnchoked(Bob)).Should().Contain(request => request.PieceIndex == 0);
    }

    [Fact]
    public void PeerUnchoked_DoesNotAskTwoPeersForTheSameBlockOutsideEndgame()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0));
        coordinator.PeerAnnouncedBitfield(Bob, Holding(4, 0));

        RequestsIn(coordinator.PeerUnchoked(Alice)).Should().ContainSingle();
        RequestsIn(coordinator.PeerUnchoked(Bob)).Should().BeEmpty();
    }

    [Fact]
    public void PeerUnchoked_AsksSeveralPeersForTheSameBlockInEndgame()
    {
        TorrentMetadata metadata = Metadata();
        TorrentCoordinator coordinator = new(metadata, Holding(4, 0, 1, 2), SwarmPolicy.Default);

        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 3));
        coordinator.PeerAnnouncedBitfield(Bob, Holding(4, 3));

        // One piece left of four is endgame. A single slow peer must not be able to
        // park the download at ninety-nine percent.
        RequestsIn(coordinator.PeerUnchoked(Alice)).Should().ContainSingle();
        RequestsIn(coordinator.PeerUnchoked(Bob)).Should().ContainSingle();
    }

    [Fact]
    public void IsComplete_TurnsTrueOnlyWhenEveryPieceIsIn()
    {
        TorrentCoordinator coordinator = Fresh();
        coordinator.PeerAnnouncedBitfield(Alice, Holding(4, 0, 1, 2, 3));
        coordinator.PeerUnchoked(Alice);

        for (int index = 0; index < 3; index++)
        {
            coordinator.BlockReceived(Alice, index, 0, Piece(index));
            coordinator.IsComplete.Should().BeFalse();
            coordinator.PeerUnchoked(Alice);
        }

        coordinator.BlockReceived(Alice, 3, 0, Piece(3));

        coordinator.IsComplete.Should().BeTrue();
    }
}
