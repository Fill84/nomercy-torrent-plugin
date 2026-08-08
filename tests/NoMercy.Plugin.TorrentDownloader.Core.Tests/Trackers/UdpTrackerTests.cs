// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using System.Net;
using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Trackers;

public class UdpTrackerTests
{
    private const long ProtocolMagic = 0x41727101980;
    private const long ConnectionId = 0x0BADC0DEDEADBEEF;

    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();
    private static readonly byte[] PeerId = "-NM0100-abcdefghijkl"u8.ToArray();

    private static AnnounceRequest Request(AnnounceEvent announceEvent = AnnounceEvent.Started) =>
        new(InfoHash, PeerId, 6881, Downloaded: 100, Uploaded: 200, Left: 300, announceEvent);

    [Fact]
    public async Task AnnounceAsync_ConnectsBeforeItAnnounces()
    {
        FakeUdp udp = new();
        UdpTracker tracker = new(udp);

        await tracker.AnnounceAsync("udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        udp.Sent.Should().HaveCount(2);

        // A UDP tracker hands out a connection id first, and it expires. Reusing a stale
        // one gets every announce rejected, so the exchange always starts here.
        BinaryPrimitives.ReadInt64BigEndian(udp.Sent[0]).Should().Be(ProtocolMagic);
        BinaryPrimitives.ReadInt32BigEndian(udp.Sent[0].AsSpan(8)).Should().Be(0);

        BinaryPrimitives.ReadInt64BigEndian(udp.Sent[1]).Should().Be(ConnectionId);
        BinaryPrimitives.ReadInt32BigEndian(udp.Sent[1].AsSpan(8)).Should().Be(1);
    }

    [Fact]
    public async Task AnnounceAsync_SendsTheTorrentAndTheProgress()
    {
        FakeUdp udp = new();
        UdpTracker tracker = new(udp);

        await tracker.AnnounceAsync("udp://tracker.test:1337/announce", Request(AnnounceEvent.Completed), CancellationToken.None);

        byte[] announce = udp.Sent[1];

        announce.AsSpan(16, 20).ToArray().Should().Equal(InfoHash);
        announce.AsSpan(36, 20).ToArray().Should().Equal(PeerId);
        BinaryPrimitives.ReadInt64BigEndian(announce.AsSpan(56)).Should().Be(100);
        BinaryPrimitives.ReadInt64BigEndian(announce.AsSpan(64)).Should().Be(300);
        BinaryPrimitives.ReadInt64BigEndian(announce.AsSpan(72)).Should().Be(200);
        BinaryPrimitives.ReadInt32BigEndian(announce.AsSpan(80)).Should().Be(1);
        BinaryPrimitives.ReadUInt16BigEndian(announce.AsSpan(96)).Should().Be(6881);
    }

    [Fact]
    public async Task AnnounceAsync_ReadsThePeersAndTheInterval()
    {
        FakeUdp udp = new();
        udp.Peers.Add(("192.168.2.50", 6881));
        udp.Peers.Add(("10.0.0.7", 51413));
        udp.Interval = 900;

        AnnounceResult result = await new UdpTracker(udp).AnnounceAsync(
            "udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        result.Peers.Should().HaveCount(2);
        result.Peers[0].Address.ToString().Should().Be("192.168.2.50");
        result.Peers[1].Port.Should().Be(51413);
        result.Interval.Should().Be(TimeSpan.FromSeconds(900));
    }

    [Fact]
    public async Task AnnounceAsync_UsesTheHostAndPortFromTheUrl()
    {
        FakeUdp udp = new();

        await new UdpTracker(udp).AnnounceAsync("udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        udp.LastHost.Should().Be("tracker.test");
        udp.LastPort.Should().Be(1337);
    }

    [Fact]
    public async Task AnnounceAsync_SurfacesTheTrackersError()
    {
        FakeUdp udp = new() { ErrorMessage = "torrent not registered" };

        Func<Task> announce = () => new UdpTracker(udp).AnnounceAsync(
            "udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>().WithMessage("*torrent not registered*");
    }

    [Fact]
    public async Task AnnounceAsync_RefusesAReplyForADifferentTransaction()
    {
        // The transaction id is the only thing tying a UDP reply to our request. A
        // mismatch means somebody else's packet, or somebody guessing.
        FakeUdp udp = new() { ScrambleTransactionId = true };

        Func<Task> announce = () => new UdpTracker(udp).AnnounceAsync(
            "udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>().WithMessage("*transaction*");
    }

    [Fact]
    public async Task AnnounceAsync_RefusesATruncatedReply()
    {
        FakeUdp udp = new() { TruncateAnnounce = true };

        Func<Task> announce = () => new UdpTracker(udp).AnnounceAsync(
            "udp://tracker.test:1337/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>();
    }

    [Fact]
    public async Task AnnounceAsync_RejectsAUrlThatIsNotUdp()
    {
        Func<Task> announce = () => new UdpTracker(new FakeUdp()).AnnounceAsync(
            "http://tracker.test/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>();
    }

    private sealed class FakeUdp : IUdpTransport
    {
        public List<byte[]> Sent { get; } = [];
        public List<(string Address, int Port)> Peers { get; } = [];
        public int Interval { get; set; } = 1800;
        public string? ErrorMessage { get; set; }
        public bool ScrambleTransactionId { get; set; }
        public bool TruncateAnnounce { get; set; }
        public string LastHost { get; private set; } = string.Empty;
        public int LastPort { get; private set; }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] request, CancellationToken ct)
        {
            LastHost = host;
            LastPort = port;
            Sent.Add(request);

            int action = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(8));
            int transaction = BinaryPrimitives.ReadInt32BigEndian(request.AsSpan(12));

            if (ScrambleTransactionId)
                transaction ^= 0x5A5A5A5A;

            if (ErrorMessage is not null && action == 1)
            {
                byte[] error = new byte[8 + Encoding.ASCII.GetByteCount(ErrorMessage)];
                BinaryPrimitives.WriteInt32BigEndian(error, 3);
                BinaryPrimitives.WriteInt32BigEndian(error.AsSpan(4), transaction);
                Encoding.ASCII.GetBytes(ErrorMessage).CopyTo(error, 8);
                return Task.FromResult(error);
            }

            if (action == 0)
            {
                byte[] connect = new byte[16];
                BinaryPrimitives.WriteInt32BigEndian(connect, 0);
                BinaryPrimitives.WriteInt32BigEndian(connect.AsSpan(4), transaction);
                BinaryPrimitives.WriteInt64BigEndian(connect.AsSpan(8), ConnectionId);
                return Task.FromResult(connect);
            }

            if (TruncateAnnounce)
                return Task.FromResult(new byte[10]);

            byte[] announce = new byte[20 + Peers.Count * 6];
            BinaryPrimitives.WriteInt32BigEndian(announce, 1);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(4), transaction);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(8), Interval);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(12), 3);
            BinaryPrimitives.WriteInt32BigEndian(announce.AsSpan(16), 7);

            for (int index = 0; index < Peers.Count; index++)
            {
                int offset = 20 + index * 6;
                IPAddress.Parse(Peers[index].Address).GetAddressBytes().CopyTo(announce, offset);
                BinaryPrimitives.WriteUInt16BigEndian(announce.AsSpan(offset + 4), (ushort)Peers[index].Port);
            }

            return Task.FromResult(announce);
        }
    }
}
