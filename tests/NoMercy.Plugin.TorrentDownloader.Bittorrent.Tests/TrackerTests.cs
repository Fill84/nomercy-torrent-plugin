using System.Buffers.Binary;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Trackers, against what two real ones really answered.
/// </summary>
/// <remarks>
/// The fixtures are a real HTTP announce to the Internet Archive's tracker and
/// a real UDP connect and announce to opentrackr, for torrents that are freely
/// distributable. Every byte is as it arrived <em>except</em> the four address
/// bytes of each compact peer, which the capture tool replaces with TEST-NET-1:
/// the first peer a tracker names is usually this machine, and a fixture in a
/// public repository must not publish anybody's address. The lengths, the
/// order, the intervals and the counts are untouched, and those are what a
/// parser can be wrong about.
/// </remarks>
public class TrackerTests
{
    /// <remarks>
    /// Every parameter BEP 3 requires, and the two that are bytes rather than
    /// text encoded a byte at a time. Putting twenty raw bytes through a text
    /// encoder turns each one above 0x7F into two, and the tracker then answers
    /// "not authorized" for a torrent it is serving — which is what the first
    /// version of the capture tool did.
    /// </remarks>
    [Fact]
    public void AnHttpAnnounceCarriesEveryRequiredParameter()
    {
        Uri address = HttpAnnounce.Address("http://bt1.archive.org:6969/announce", Request(AnnounceEvent.Started));

        string query = address.Query;

        foreach (string required in (string[])
                 ["info_hash=", "peer_id=", "port=51413", "uploaded=0", "downloaded=0", "left=6345887744", "compact=1"])
        {
            Assert.Contains(required, query, StringComparison.Ordinal);
        }

        Assert.Contains("event=started", query, StringComparison.Ordinal);

        // The hash, percent-encoded byte by byte: this one has 0xD1 in it,
        // which a text encoder would write as two bytes.
        Assert.Contains("%D1%60%B8%D8", query, StringComparison.OrdinalIgnoreCase);
    }

    /// <remarks>
    /// An announce with nothing to report carries no event at all, which is
    /// what the tracker expects on the interval.
    /// </remarks>
    [Fact]
    public void AnAnnounceOnTheIntervalCarriesNoEvent()
    {
        Assert.DoesNotContain(
            "event=",
            HttpAnnounce.Address("http://tracker.test/announce", Request(AnnounceEvent.None)).Query,
            StringComparison.Ordinal);
    }

    /// <remarks>
    /// The real answer: its intervals, its counts and its compact peers.
    /// </remarks>
    [Fact]
    public void ARealHttpAnswerYieldsItsIntervalsCountsAndPeers()
    {
        AnnounceResponse answer = HttpAnnounce.Read(Fixture("tracker-http-announce.bin"));

        Assert.False(answer.Refused);
        Assert.Equal(TimeSpan.FromSeconds(1642), answer.Interval);
        Assert.Equal(TimeSpan.FromSeconds(821), answer.MinInterval);
        Assert.Equal(0, answer.Seeders);
        Assert.Equal(1, answer.Leechers);

        PeerAddress peer = Assert.Single(answer.Peers);

        Assert.Equal("192.0.2.1", peer.Address.ToString());
        Assert.Equal(51413, peer.Port);
    }

    /// <remarks>
    /// A tracker that refuses says so and says nothing else. Reading the peer
    /// list first would show an empty swarm where there is a reason — and this
    /// is a real refusal, from Ubuntu's tracker, for a torrent it would not
    /// announce.
    /// </remarks>
    [Fact]
    public void ARefusalIsReadAsARefusalAndNotAsAnEmptySwarm()
    {
        AnnounceResponse answer = HttpAnnounce.Read(Fixture("tracker-http-failure.bin"));

        Assert.True(answer.Refused);
        Assert.Contains("not authorized", answer.Failure!, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(answer.Peers);
    }

    /// <remarks>
    /// The connect request is BEP 15's magic, action nought, and a transaction
    /// id the tracker echoes. A tracker that does not see the magic answers
    /// nothing at all, which reads exactly like a tracker that is down.
    /// </remarks>
    [Fact]
    public void AUdpConnectRequestIsTheMagicTheActionAndATransactionId()
    {
        byte[] datagram = UdpAnnounce.ConnectRequest(0x1234ABCD);

        Assert.Equal(16, datagram.Length);
        Assert.Equal(0x41727101980L, BinaryPrimitives.ReadInt64BigEndian(datagram));
        Assert.Equal(0, BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(8)));
        Assert.Equal(0x1234ABCD, BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(12)));
    }

    /// <remarks>
    /// And the real answer to it carries the connection id everything after it
    /// depends on.
    /// </remarks>
    [Fact]
    public void ARealConnectAnswerYieldsItsConnectionId()
    {
        long id = UdpAnnounce.ReadConnect(Fixture("tracker-udp-connect.bin"), 0x1234ABCD);

        Assert.Equal(unchecked((long)0xC8CF518E1F2AA572), id);
    }

    /// <remarks>
    /// An answer to somebody else's question arrives at this socket looking
    /// exactly like an answer to ours: UDP has no connection, and the
    /// transaction id is the only thing that tells them apart.
    /// </remarks>
    [Fact]
    public void AnAnswerToSomebodyElsesQuestionIsRefused()
    {
        Assert.Throws<TrackerException>(
            () => UdpAnnounce.ReadConnect(Fixture("tracker-udp-connect.bin"), 0x0BADF00D));
    }

    /// <remarks>
    /// The real announce answer: interval, leechers, seeders, and ten peers at
    /// six bytes each.
    /// </remarks>
    [Fact]
    public void ARealUdpAnnounceAnswerYieldsItsCountsAndItsPeers()
    {
        AnnounceResponse answer = UdpAnnounce.ReadAnnounce(Fixture("tracker-udp-announce.bin"), 0x1234ABCE);

        Assert.Equal(TimeSpan.FromSeconds(3635), answer.Interval);
        Assert.Equal(46, answer.Seeders);
        Assert.Equal(13, answer.Leechers);
        Assert.Equal(10, answer.Peers.Count);

        Assert.Equal("192.0.2.1", answer.Peers[0].Address.ToString());
        Assert.Equal(20048, answer.Peers[0].Port);
        Assert.Equal(6881, answer.Peers[2].Port);
    }

    /// <remarks>
    /// The announce request is ninety-eight bytes with the connection id in
    /// front and the port at the end, and the event is a number rather than a
    /// word.
    /// </remarks>
    [Fact]
    public void AUdpAnnounceRequestIsNinetyEightBytesInTheRightOrder()
    {
        byte[] datagram = UdpAnnounce.AnnounceRequest(0x0102030405060708L, 7, Request(AnnounceEvent.Completed));

        Assert.Equal(98, datagram.Length);
        Assert.Equal(0x0102030405060708L, BinaryPrimitives.ReadInt64BigEndian(datagram));
        Assert.Equal(1, BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(8)));
        Assert.Equal(7, BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(12)));

        // Downloaded, left, uploaded — in that order, which is not the order
        // anybody would guess.
        Assert.Equal(0, BinaryPrimitives.ReadInt64BigEndian(datagram.AsSpan(56)));
        Assert.Equal(6345887744, BinaryPrimitives.ReadInt64BigEndian(datagram.AsSpan(64)));

        Assert.Equal((int)AnnounceEvent.Completed, BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(80)));
        Assert.Equal(51413, BinaryPrimitives.ReadUInt16BigEndian(datagram.AsSpan(96)));
    }

    /// <remarks>
    /// <c>15 * 2^n</c> seconds, up to eight tries — a quarter of an hour by the
    /// last one. UDP loses datagrams silently, so a client that gave up after
    /// one would call a working tracker dead.
    /// </remarks>
    [Fact]
    public void TheUdpBackoffIsFifteenSecondsDoubling()
    {
        Assert.Equal(TimeSpan.FromSeconds(15), UdpAnnounce.Backoff(0));
        Assert.Equal(TimeSpan.FromSeconds(30), UdpAnnounce.Backoff(1));
        Assert.Equal(TimeSpan.FromSeconds(60), UdpAnnounce.Backoff(2));
        Assert.Equal(TimeSpan.FromSeconds(15 * 128), UdpAnnounce.Backoff(7));

        Assert.Equal(8, UdpAnnounce.Tries);

        // And it does not keep doubling past the last try.
        Assert.Equal(UdpAnnounce.Backoff(7), UdpAnnounce.Backoff(99));
    }

    /// <remarks>
    /// A connection id is asked for once and used for the minute BEP 15 allows.
    /// Asking before every announce doubles every announce; using one past its
    /// minute earns an error rather than peers.
    /// </remarks>
    [Fact]
    public async Task AConnectionIdIsReusedWithinItsMinuteAndRenewedAfter()
    {
        FakeTimeProvider clock = new();
        FakeTransport transport = new();

        TrackerSet trackers = new(transport, clock);

        await trackers.AnnounceOneAsync(Udp, Request(AnnounceEvent.Started), CancellationToken.None);
        Assert.Equal(1, transport.Connects);

        clock.Advance(TimeSpan.FromSeconds(59));
        await trackers.AnnounceOneAsync(Udp, Request(AnnounceEvent.None), CancellationToken.None);
        Assert.Equal(1, transport.Connects);

        clock.Advance(TimeSpan.FromSeconds(2));
        await trackers.AnnounceOneAsync(Udp, Request(AnnounceEvent.None), CancellationToken.None);
        Assert.Equal(2, transport.Connects);
    }

    /// <remarks>
    /// Every tracker at once, and one that will not answer costs only itself. A
    /// torrent with six trackers where the first is down is a torrent that
    /// still has five.
    /// </remarks>
    [Fact]
    public async Task OneTrackerThatFailsDoesNotStopTheOthers()
    {
        FakeTransport transport = new();
        transport.Refuse("http://down.test/announce");

        IReadOnlyList<TrackerResult> results = await new TrackerSet(transport, TimeProvider.System).AnnounceAsync(
            ["http://down.test/announce", "http://bt1.archive.org:6969/announce", Udp],
            Request(AnnounceEvent.Started),
            CancellationToken.None);

        Assert.Equal(3, results.Count);

        Assert.NotNull(results[0].Failure);
        Assert.Null(results[0].Response);

        Assert.NotNull(results[1].Response);
        Assert.NotEmpty(results[1].Response!.Peers);

        Assert.NotNull(results[2].Response);
    }

    /// <remarks>
    /// Every tracker at once. Announced one after another, a torrent with six
    /// trackers where two are slow spends the sum of their patience before the
    /// third is even asked — and every one of them has an interval it expects
    /// to be asked on. The transport holds each request until all of them have
    /// arrived, which cannot happen unless they were sent together.
    /// </remarks>
    [Fact]
    public async Task EveryTrackerIsAnnouncedToAtOnce()
    {
        FakeTransport transport = new() { Expected = 3 };

        Task<IReadOnlyList<TrackerResult>> announcing = new TrackerSet(transport, TimeProvider.System).AnnounceAsync(
            ["http://one.test/announce", "http://two.test/announce", "http://three.test/announce"],
            Request(AnnounceEvent.Started),
            CancellationToken.None);

        // Bounded: announcing one at a time never gets here, and an unbounded
        // wait would hang the suite rather than fail it.
        await Task.WhenAny(transport.AllInFlight, Task.Delay(TimeSpan.FromSeconds(10)));

        Assert.True(
            transport.AllInFlight.IsCompletedSuccessfully,
            "The trackers were announced to one after another.");

        transport.Release();

        Assert.Equal(3, (await announcing).Count);
    }

    private const string Udp = "udp://tracker.opentrackr.org:1337/announce";

    private static AnnounceRequest Request(AnnounceEvent what)
    {
        return new(
            Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7"),
            Encoding.ASCII.GetBytes("-NM0400-abcdefghijkl"),
            51413,
            Uploaded: 0,
            Downloaded: 0,
            Left: 6345887744,
            what,
            NumWant: 50);
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    /// <remarks>
    /// <para>
    /// One tracker that never answers is one tracker. BEP 15 says to wait
    /// <c>15 * 2^n</c> seconds over eight tries, which is half an hour on a
    /// dead one — and the announce is a single <c>Task.WhenAll</c>, so every
    /// tracker with peers to hand over waited that half hour too. Worse, the
    /// cancellation that ended it went straight past the catch, so
    /// <c>AnnounceAsync</c> threw and the caller got nothing from anybody.
    /// </para>
    /// <para>
    /// Measured on 31 August 2026 against the owner's Dark Matter pack, whose
    /// magnet carries eighteen trackers with several years dead: no announce
    /// came back at all for the first two and a half minutes, and the torrent
    /// sat at no peers with an empty swarm column while four trackers had three
    /// hundred seeds to give.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ATrackerThatNeverAnswersDoesNotTakeTheOthersDownWithIt()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 31, 20, 0, 0, TimeSpan.Zero));
        SilentAndSpeaking wire = new();

        TrackerSet trackers = new(wire, clock);

        Task<IReadOnlyList<TrackerResult>> announcing = trackers.AnnounceAsync(
            ["http://silent.test/announce", "http://speaking.test/announce"],
            Request(AnnounceEvent.Started),
            CancellationToken.None);

        await wire.Speaking;

        // Long past the one tracker's deadline and nowhere near BEP 15's eight
        // tries, which is the whole point: the others are not made to wait it
        // out, and neither is the caller.
        clock.Advance(TrackerSet.Deadline + TimeSpan.FromSeconds(1));

        IReadOnlyList<TrackerResult> answers = await announcing;

        Assert.Equal(2, answers.Count);

        // The one that spoke is heard, with its peers.
        Assert.NotNull(Assert.Single(answers, one => one.Tracker.Contains("speaking", StringComparison.Ordinal)).Response);

        // And the one that did not says so, rather than throwing.
        TrackerResult silent = Assert.Single(answers, one => one.Tracker.Contains("silent", StringComparison.Ordinal));

        Assert.Null(silent.Response);
        Assert.NotNull(silent.Failure);
    }

    /// <summary>One tracker that answers at once and one that never does.</summary>
    private sealed class SilentAndSpeaking : ITrackerTransport
    {
        private readonly TaskCompletionSource _spoken = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Completes once the tracker that answers has answered.</summary>
        public Task Speaking => _spoken.Task;

        public async Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            if (address.Host.Contains("silent", StringComparison.Ordinal))
            {
                // Never. Not a refusal, not an error — the silence of a machine
                // that has not been there for years.
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }

            _spoken.TrySetResult();

            return Encoding.ASCII.GetBytes("d8:intervali1800e5:peers0:e");
        }

        public Task<byte[]> ExchangeAsync(
            string host,
            int port,
            byte[] datagram,
            TimeSpan patience,
            CancellationToken ct)
        {
            throw new NotSupportedException("This test is over HTTP.");
        }
    }

    /// <summary>
    /// The wire, answering with what real trackers really sent.
    /// </summary>
    /// <remarks>
    /// It decides nothing. Which tracker is asked, when, and what happens when
    /// one will not answer are all decided above it, and that is the part being
    /// tested.
    /// </remarks>
    private sealed class FakeTransport : ITrackerTransport
    {
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
        private readonly TaskCompletionSource _allInFlight = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _held = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;

        public int Connects { get; private set; }

        /// <summary>How many are expected to be in flight at once, when a test cares.</summary>
        public int Expected { get; init; }

        /// <summary>Completes when that many are all waiting together.</summary>
        public Task AllInFlight => _allInFlight.Task;

        /// <summary>Lets them all answer.</summary>
        public void Release()
        {
            _held.TrySetResult();
        }

        public void Refuse(string tracker)
        {
            _refused.Add(tracker);
        }

        public async Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            if (_refused.Any(one => address.ToString().StartsWith(one, StringComparison.OrdinalIgnoreCase)))
            {
                throw new HttpRequestException("nothing answered");
            }

            if (Expected > 0)
            {
                if (Interlocked.Increment(ref _inFlight) == Expected)
                {
                    _allInFlight.TrySetResult();
                }

                await _held.Task;
            }

            return Fixture("tracker-http-announce.bin");
        }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
        {
            int action = BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(8));
            int transaction = BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(12));

            byte[] answer = action == 0
                ? Fixture("tracker-udp-connect.bin")
                : Fixture("tracker-udp-announce.bin");

            if (action == 0)
            {
                Connects++;
            }

            // The transaction id this test's client chose, echoed as a real
            // tracker echoes it.
            BinaryPrimitives.WriteInt32BigEndian(answer.AsSpan(4), transaction);

            return Task.FromResult(answer);
        }
    }
}
