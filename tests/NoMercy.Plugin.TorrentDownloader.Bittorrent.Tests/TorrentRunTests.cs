using System.Buffers.Binary;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// One torrent's whole life, driven.
/// </summary>
/// <remarks>
/// Sprint 5 built the trackers, the peer wire, the pieces, the disk, the
/// metadata exchange and the session, and <c>BittorrentEngine</c> — the only
/// implementation of the port the plugin calls — joined none of them: it parsed
/// a magnet, recorded the hash and stopped. This is the loop that runs them, and
/// every rule here is one without which nothing downloads.
/// </remarks>
public class TorrentRunTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-run-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Every tracker, and the peers each names. A magnet is a hash and a list
    /// of trackers and nothing else: without the announce there is nobody to
    /// ask for the metadata, so the torrent sits saying "fetching metadata" for
    /// as long as the server runs.
    /// </remarks>
    [Fact]
    public async Task EveryTrackerIsAnnouncedToAndEveryPeerTheyNameIsDialled()
    {
        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);

        Assert.Equal(
            ["http://one.example/announce", "http://two.example/announce"],
            trackers.Asked.Order());

        // One peer per tracker answer, and the same peer from two trackers is
        // one peer: dialling it twice is two connections to one client, which
        // is how a swarm of six looks like a swarm of twelve.
        PeerAddress dialled = Assert.Single(dialler.Dialled);

        Assert.Equal("192.0.2.1", dialled.Address.ToString());
        Assert.Equal(51413, dialled.Port);
    }

    /// <remarks>
    /// A tracker that will not answer costs that tracker. A torrent with six
    /// trackers where the first is down is a torrent that still has five, and
    /// stopping at the first refusal is how a swarm with hundreds of peers came
    /// to look empty.
    /// </remarks>
    [Fact]
    public async Task ATrackerThatWillNotAnswerCostsThatTrackerAndNothingElse()
    {
        AnsweringTrackers trackers = new();
        trackers.Refuse("http://one.example/announce");

        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);

        Assert.Single(dialler.Dialled);
    }

    /// <remarks>
    /// Nothing is dialled twice. A peer already connected is one the next
    /// announce names again, and re-dialling it every interval is a client that
    /// opens a connection a minute to the same machine until it is banned.
    /// </remarks>
    [Fact]
    public async Task APeerAlreadyConnectedIsNotDialledAgainOnTheNextAnnounce()
    {
        AnsweringTrackers trackers = new();
        RecordingDialler dialler = new();

        using TorrentRun run = Run(trackers, dialler);

        await run.OnceAsync(CancellationToken.None);
        await run.OnceAsync(CancellationToken.None);

        Assert.Single(dialler.Dialled);
    }

    private TorrentRun Run(AnsweringTrackers transport, RecordingDialler dialler)
    {
        return new(
            Hash,
            ["http://one.example/announce", "http://two.example/announce"],
            _folder,
            new TrackerSet(transport, TimeProvider.System),
            dialler,
            Id("NM0001"),
            listenPort: 51413,
            TimeProvider.System);
    }

    private static byte[] Hash => [.. Enumerable.Range(0, 20).Select(one => (byte)one)];

    private static byte[] Id(string name)
    {
        byte[] id = new byte[20];

        System.Text.Encoding.ASCII.GetBytes(name).CopyTo(id, 0);

        return id;
    }

    /// <summary>Trackers that answer with a real captured announce.</summary>
    private sealed class AnsweringTrackers : ITrackerTransport
    {
        private readonly HashSet<string> _refused = new(StringComparer.OrdinalIgnoreCase);
        private readonly Lock _lock = new();
        private readonly List<string> _asked = [];

        /// <summary>Every tracker address that was really fetched.</summary>
        public IReadOnlyList<string> Asked
        {
            get
            {
                lock (_lock)
                {
                    return [.. _asked];
                }
            }
        }

        public void Refuse(string tracker)
        {
            _refused.Add(tracker);
        }

        public Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            string tracker = address.GetLeftPart(UriPartial.Path);

            lock (_lock)
            {
                _asked.Add(tracker);
            }

            return _refused.Contains(tracker)
                ? throw new HttpRequestException("nothing answered")
                : Task.FromResult(Fixture("tracker-http-announce.bin"));
        }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
        {
            int action = BinaryPrimitives.ReadInt32BigEndian(datagram.AsSpan(8));

            return Task.FromResult(Fixture(action == 0 ? "tracker-udp-connect.bin" : "tracker-udp-announce.bin"));
        }
    }

    /// <summary>A dialler that records who it was asked for.</summary>
    private sealed class RecordingDialler : IPeerDialler
    {
        private readonly Lock _lock = new();
        private readonly List<PeerAddress> _dialled = [];

        public IReadOnlyList<PeerAddress> Dialled
        {
            get
            {
                lock (_lock)
                {
                    return [.. _dialled];
                }
            }
        }

        public Task<PeerConnection?> DialAsync(
            PeerAddress peer,
            byte[] infoHash,
            byte[] peerId,
            int pieces,
            CancellationToken ct)
        {
            lock (_lock)
            {
                _dialled.Add(peer);
            }

            // Null is a peer that would not talk, which is most of the
            // addresses a tracker gives out.
            return Task.FromResult<PeerConnection?>(null);
        }
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(
            directory?.FullName ?? throw new InvalidOperationException("no solution folder above the test assembly"),
            "tests",
            "fixtures",
            name));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }
}
