// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Trackers;

public class HttpTrackerTests
{
    private static readonly byte[] InfoHash =
        [0x12, 0x34, 0x56, 0x78, 0x9A, 0xBC, 0xDE, 0xF0, 0x00, 0x20, 0x41, 0x7E, 0x2D, 0x5F, 0x2E, 0x7A, 0xFF, 0x01, 0x02, 0x03];

    private static readonly byte[] PeerId = "-NM0100-abcdefghijkl"u8.ToArray();

    private static AnnounceRequest Request() => new(InfoHash, PeerId, 6881, Downloaded: 0, Uploaded: 0, Left: 1024, AnnounceEvent.Started);

    private static byte[] CompactResponse(params (string Address, int Port)[] peers)
    {
        List<byte> compact = [];

        foreach ((string address, int port) in peers)
        {
            compact.AddRange(IPAddress.Parse(address).GetAddressBytes());
            compact.Add((byte)(port >> 8));
            compact.Add((byte)(port & 0xFF));
        }

        return BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["interval"] = new BInteger(1800),
            ["peers"] = new BBytes([.. compact]),
        }));
    }

    [Fact]
    public async Task AnnounceAsync_ReadsACompactPeerList()
    {
        FakeHandler handler = new(CompactResponse(("192.168.2.50", 6881), ("10.0.0.7", 51413)));
        HttpTracker tracker = new(new HttpClient(handler));

        AnnounceResult result = await tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        result.Peers.Should().HaveCount(2);
        result.Peers[0].Address.ToString().Should().Be("192.168.2.50");
        result.Peers[0].Port.Should().Be(6881);
        result.Peers[1].Address.ToString().Should().Be("10.0.0.7");
        result.Peers[1].Port.Should().Be(51413);
        result.Interval.Should().Be(TimeSpan.FromSeconds(1800));
    }

    [Fact]
    public async Task AnnounceAsync_ReadsTheOlderDictionaryPeerList()
    {
        byte[] response = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["interval"] = new BInteger(900),
            ["peers"] = new BList([new BDictionary(new Dictionary<string, BValue>
            {
                ["ip"] = new BBytes("192.168.2.51"u8.ToArray()),
                ["port"] = new BInteger(6969),
            })]),
        }));

        HttpTracker tracker = new(new HttpClient(new FakeHandler(response)));

        AnnounceResult result = await tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        result.Peers.Should().ContainSingle();
        result.Peers[0].Port.Should().Be(6969);
    }

    [Fact]
    public async Task AnnounceAsync_PercentEncodesEveryByteOfTheInfoHash()
    {
        FakeHandler handler = new(CompactResponse());
        HttpTracker tracker = new(new HttpClient(handler));

        await tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        // The info hash is raw bytes, not text. A tracker that receives a mangled
        // encoding answers about a torrent nobody has - and the usual URL helpers
        // get this wrong on the bytes that happen to be printable.
        handler.LastUrl.Should().Contain("info_hash=%124Vx%9a%bc%de%f0%00%20A~-_.z%ff%01%02%03");
    }

    [Fact]
    public async Task AnnounceAsync_SendsTheProgressAndTheEvent()
    {
        FakeHandler handler = new(CompactResponse());
        HttpTracker tracker = new(new HttpClient(handler));

        await tracker.AnnounceAsync(
            "http://tracker.test/announce",
            new AnnounceRequest(InfoHash, PeerId, 6881, Downloaded: 4096, Uploaded: 0, Left: 2048, AnnounceEvent.Completed),
            CancellationToken.None);

        handler.LastUrl.Should().Contain("downloaded=4096");
        handler.LastUrl.Should().Contain("left=2048");
        handler.LastUrl.Should().Contain("event=completed");
        handler.LastUrl.Should().Contain("compact=1");
    }

    [Fact]
    public async Task AnnounceAsync_KeepsAnExistingQueryStringOnTheAnnounceUrl()
    {
        FakeHandler handler = new(CompactResponse());
        HttpTracker tracker = new(new HttpClient(handler));

        // A private tracker's passkey usually rides in the announce URL already.
        await tracker.AnnounceAsync("http://tracker.test/announce?passkey=secret", Request(), CancellationToken.None);

        handler.LastUrl.Should().Contain("passkey=secret");
        handler.LastUrl.Should().Contain("&info_hash=");
    }

    [Fact]
    public async Task AnnounceAsync_SurfacesTheTrackersOwnRefusal()
    {
        byte[] response = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["failure reason"] = new BBytes("unregistered torrent"u8.ToArray()),
        }));

        HttpTracker tracker = new(new HttpClient(new FakeHandler(response)));

        Func<Task> announce = () => tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>().WithMessage("*unregistered torrent*");
    }

    [Fact]
    public async Task AnnounceAsync_TreatsAServerErrorAsATrackerFailure()
    {
        HttpTracker tracker = new(new HttpClient(new FakeHandler([], HttpStatusCode.InternalServerError)));

        Func<Task> announce = () => tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>();
    }

    [Fact]
    public async Task AnnounceAsync_RejectsACompactListThatIsNotAMultipleOfSix()
    {
        byte[] response = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["interval"] = new BInteger(1800),
            ["peers"] = new BBytes([1, 2, 3, 4, 5]),
        }));

        HttpTracker tracker = new(new HttpClient(new FakeHandler(response)));

        Func<Task> announce = () => tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        await announce.Should().ThrowAsync<TrackerException>();
    }

    [Fact]
    public async Task AnnounceAsync_FallsBackToASaneIntervalWhenTheTrackerOmitsOne()
    {
        byte[] response = BencodeWriter.Write(new BDictionary(new Dictionary<string, BValue>
        {
            ["peers"] = new BBytes([]),
        }));

        HttpTracker tracker = new(new HttpClient(new FakeHandler(response)));

        AnnounceResult result = await tracker.AnnounceAsync("http://tracker.test/announce", Request(), CancellationToken.None);

        result.Interval.Should().Be(TimeSpan.FromMinutes(30));
    }

    private sealed class FakeHandler(byte[] response, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public string LastUrl { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri!.ToString();

            return Task.FromResult(new HttpResponseMessage(status) { Content = new ByteArrayContent(response) });
        }
    }
}
