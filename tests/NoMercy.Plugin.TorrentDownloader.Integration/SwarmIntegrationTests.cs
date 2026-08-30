
using System.Globalization;
using System.Net;
using System.Net.Sockets;

using Microsoft.Extensions.Logging;

using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Integration;

/// <summary>
/// The engine reaches a swarm that is demonstrably there.
/// </summary>
/// <remarks>
/// <strong>Both of these need a machine that can dial out.</strong> They are
/// integration tests and excluded from the ordinary run for that reason: on a
/// host whose outbound ports are shut, every peer times out and the engine test
/// fails for the network rather than for the code. Run them where the plugin
/// itself runs.
/// </remarks>
/// <remarks>
/// <para>
/// On 26 August 2026 a magnet the owner pasted by hand — twelve trackers on it,
/// among them opentrackr, demonii and torrent.eu.org — sat at "fetching
/// metadata" with no peer and no seed until it timed out. Announcing that same
/// info hash to that same tracker with this plugin's own code answered
/// <c>seeders 1206, leechers 462</c> and handed back ten peers, so neither the
/// swarm nor the announce was at fault.
/// </para>
/// <para>
/// This is the reproduction that fault needs: the real engine, the real
/// transport, the real dialler, and one magnet whose swarm is large enough that
/// finding nobody cannot be bad luck.
/// </para>
/// </remarks>
public class SwarmIntegrationTests
{
    /// <summary>Counts what the engine asked of the dialler, and what came back.</summary>
    private sealed class Counted(IPeerDialler inner) : IPeerDialler
    {
        public int Attempted;
        public int Answered;

        public async Task<PeerConnection?> DialAsync(
            PeerAddress peer,
            byte[] infoHash,
            byte[] peerId,
            int pieces,
            CancellationToken ct)
        {
            Interlocked.Increment(ref Attempted);

            PeerConnection? talked = await inner.DialAsync(peer, infoHash, peerId, pieces, ct);

            if (talked is not null)
            {
                Interlocked.Increment(ref Answered);
            }

            return talked;
        }
    }

    /// <summary>Everything the engine said, so a silent failure is not silent.</summary>
    private sealed class Told : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel level) => true;

        public void Log<TState>(
            LogLevel level,
            EventId id,
            TState state,
            Exception? wrong,
            Func<TState, Exception?, string> format)
        {
            lock (Lines)
            {
                Lines.Add($"{level}: {format(state, wrong)}");
            }
        }
    }

    /// <summary>The swarm this fault was found in, measured rather than assumed.</summary>
    /// <remarks>
    /// The magnet the owner pasted by hand on 26 August 2026. Announcing this
    /// hash to this tracker with this plugin's own code answered
    /// <c>seeders 1206, leechers 462</c> and handed back ten peers, so a client
    /// that finds nobody here has not been unlucky.
    /// </remarks>
    private const string Hash = "4319E25E0603C6D838C77688550308A2508026AD";

    private const string Tracker = "udp://tracker.opentrackr.org:1337/announce";

    /// <summary>
    /// Whether this machine may dial the wider internet.
    /// </summary>
    /// <remarks>
    /// Asked for rather than assumed. Both tests below need outbound
    /// connections to arbitrary peer ports, and a host that has none — the CI
    /// runner, or a developer's machine with everything but one port shut —
    /// fails them for the network rather than for the plugin. A red test that
    /// says nothing about the code is worse than no test, so these run only
    /// where somebody has said the ports are open:
    ///
    /// <c>NOMERCY_SWARM=1 dotnet test tests/NoMercy.Plugin.TorrentDownloader.Integration</c>
    /// </remarks>
    /// <summary>
    /// The port this machine really accepts on, announced so peers can dial
    /// back. A client that announces a shut port is only ever reachable to the
    /// peers it dials first, which in a swarm behind NAT is most of nobody.
    /// </summary>
    private static int ListenPort =>
        int.TryParse(Environment.GetEnvironmentVariable("NOMERCY_SWARM_PORT"), out int port)
            ? port
            : 51413;

    private static bool CanDialOut =>
        string.Equals(Environment.GetEnvironmentVariable("NOMERCY_SWARM"), "1", StringComparison.Ordinal);

    private const string Magnet =
        "magnet:?xt=urn:btih:" + Hash
        + "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce"
        + "&tr=udp%3A%2F%2Fopen.demonii.com%3A1337%2Fannounce"
        + "&tr=udp%3A%2F%2Ftracker.torrent.eu.org%3A451%2Fannounce"
        + "&tr=udp%3A%2F%2Fopen.stealth.si%3A80%2Fannounce";

    /// <remarks>
    /// The announce on its own, so that a swarm nobody reaches can be told
    /// apart from a swarm nobody asked for. This is the half above the dial.
    /// </remarks>
    [Fact]
    public async Task IntegrationTheTrackersHandBackPeersForThatSwarm()
    {
        if (!CanDialOut)
        {
            return;
        }

        using HttpClient http = new();
        using CancellationTokenSource stopping = new(TimeSpan.FromMinutes(2));

        TrackerSet trackers = new(new SocketTrackerTransport(http), TimeProvider.System);

        AnnounceRequest asking = new(
            Convert.FromHexString(Hash),
            PeerIdentity.New(),
            ListenPort,
            Uploaded: 0,
            Downloaded: 0,
            Left: 1L << 40,
            Event: AnnounceEvent.Started);

        IReadOnlyList<TrackerResult> answers = await trackers.AnnounceAsync(
            [Tracker],
            asking,
            stopping.Token);

        string said = string.Join(
            Environment.NewLine,
            answers.Select(one =>
                $"{one.Tracker}: peers {one.Response?.Peers.Count.ToString() ?? "-"}, "
                + $"seeders {one.Response?.Seeders.ToString() ?? "-"}, failure {one.Failure ?? "none"}"));

        Assert.True(answers.Any(one => one.Response is { Peers.Count: > 0 }), said);
    }

    [Fact]
    public async Task IntegrationTheEngineFindsPeersForASwarmThatIsDefinitelyThere()
    {
        if (!CanDialOut)
        {
            return;
        }

        Told said = new();
        Counted counted = new(new SocketPeerDialler(SocketPeerDialler.DefaultPatience, PeerEncryption.Allowed));

        using HttpClient http = new();
        using CancellationTokenSource stopping = new(TimeSpan.FromMinutes(6));

        using BittorrentEngine engine = new(
            listenPort: ListenPort,
            metadataTimeout: TimeSpan.FromMinutes(6),
            stallLimit: TimeSpan.FromMinutes(5),
            maxConcurrent: 1,
            seeding: new SeedLimit(0, TimeSpan.Zero),
            maxDownloadRate: 0,
            maxUploadRate: 0,
            mapping: null,
            journal: new ActivityJournal(TimeProvider.System),
            logger: said,
            transport: new SocketTrackerTransport(http),
            dialler: counted);

        engine.Start();

        string folder = Path.Combine(Path.GetTempPath(), "nomercy-swarm-" + Guid.NewGuid().ToString("n")[..8]);

        Directory.CreateDirectory(folder);

        try
        {
            TorrentHandle taken = await engine.AddAsync(new(Magnet, [], folder, null), stopping.Token);

            // Long enough for a connect, an announce and a handshake on a slow
            // link, and far short of the metadata timeout.
            TorrentStatus? seen = null;

            for (int second = 0; second < 240; second++)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stopping.Token);

                seen = (await engine.StatusAsync(stopping.Token))
                    .FirstOrDefault(one => string.Equals(one.InfoHash, taken.InfoHash, StringComparison.OrdinalIgnoreCase));

                if (seen is { Peers: > 0 })
                {
                    break;
                }
            }

            Assert.NotNull(seen);

            Assert.True(
                seen.Peers > 0,
                $"the engine found nobody in four minutes: state {seen.State}, "
                + $"peers {seen.Peers}, seeds {seen.Seeds}, error {seen.Error ?? "none"}, "
                + $"dials attempted {counted.Attempted}, answered {counted.Answered}"
                + Environment.NewLine
                + string.Join(Environment.NewLine, said.Lines));
        }
        finally
        {
            // Best effort. Passing this test means the engine reached the
            // metadata and started writing the file, so the file is open and
            // Windows will not have it deleted — which is a success, not a
            // failure, and must not be reported as one.
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Still downloading. The folder is in temp and the machine
                // clears it.
            }
        }
    }

    /// <remarks>
    /// <para>
    /// The DHT against the real network. A tracker hands out fifty addresses
    /// and most are stale; this is where every other client finds the hundreds
    /// that are there, and until 0.3.16 this one asked nobody — <c>Dht</c> was
    /// written, tested and never constructed.
    /// </para>
    /// <para>
    /// Joining is what is asserted, not a peer count: which peers the network
    /// holds for one torrent is nobody's to promise, and a table with nodes in
    /// it is the thing that was missing.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task IntegrationTheDhtJoinsTheNetworkAndCanBeAsked()
    {
        if (!CanDialOut)
        {
            return;
        }

        using CancellationTokenSource stopping = new(TimeSpan.FromMinutes(4));
        using SocketDhtTransport socket = new();

        NodeId me = NodeId.Random();

        Dht dht = new(me, new RoutingTable(me), socket);

        List<IPEndPoint> bootstrap = [];

        foreach (string address in Dht.BootstrapNodes)
        {
            string[] parts = address.Split(':');

            foreach (IPAddress found in await Dns.GetHostAddressesAsync(parts[0], stopping.Token))
            {
                if (found.AddressFamily == AddressFamily.InterNetwork)
                {
                    bootstrap.Add(new(found, int.Parse(parts[1], CultureInfo.InvariantCulture)));
                }
            }
        }

        Assert.NotEmpty(bootstrap);

        await dht.BootstrapAsync(bootstrap, stopping.Token);

        Assert.True(dht.Table.Count > 0, "the DHT bootstrapped into a table that knows nobody");

        // And a real search over it, on a torrent published to be found.
        TorrentMetadata torrent = TorrentMetadata.Read(
            await File.ReadAllBytesAsync(Fixture("ubuntu-desktop.torrent"), stopping.Token));

        PeerSearch walked = await dht.PeersAsync(torrent, 50, stopping.Token);

        Assert.True(
            walked.Asked > 0,
            $"the search asked nobody: {dht.Table.Count} nodes in the table");
    }

    /// <summary>A captured torrent, from the repository's own fixtures.</summary>
    private static string Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return Path.Combine(directory!.FullName, "tests", "fixtures", name);
    }
}
