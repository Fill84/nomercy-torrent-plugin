using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Sprint 5's acceptance, through the port the plugin actually calls.
/// </summary>
/// <remarks>
/// <para>
/// <c>S5-13</c> proved two <c>TorrentSession</c>s could do this between
/// themselves. This is the same thing one layer up: two
/// <see cref="BittorrentEngine"/>s over a real socket, reached only through
/// <see cref="ITorrentEngine"/>, which is the only door the pipeline has.
/// Everything between the two was written believing it worked, and for a whole
/// sprint it did not.
/// </para>
/// <para>
/// One end is given the torrent, the finished files and a resume file that says
/// so, which is a client that has already downloaded it. The other is given the
/// same torrent, an empty folder, and a tracker that names the first. Nothing
/// else is arranged: the leecher announces, dials what it is told, handshakes,
/// asks and writes, all on its own.
/// </para>
/// </remarks>
public class ClientAcceptanceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-accept-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task OneInstanceOfThisClientDownloadsATorrentFromAnotherThroughThePort()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(120));

        // A torrent over a real file — the fixture's own bytes — cut into
        // pieces small enough that a test finishes and numerous enough that the
        // picker, the assembly and the disk all really run.
        byte[] content = Fixture("archive-multifile.torrent");
        TorrentMetadata torrent = Synthetic(content, pieceLength: 2048);

        Assert.InRange(torrent.PieceCount, 4, 100);

        string file = Path.Combine(_root, "the.torrent");

        Directory.CreateDirectory(_root);
        File.WriteAllBytes(file, Torrent(content, torrent.PieceLength));

        Seeded(torrent, content, Folder("seed"));

        using BittorrentEngine seeding = Engine("seed", new SilentTrackers());

        seeding.Start();

        await seeding.AddAsync(new(file, [], Folder("seed"), torrent.TotalLength), stopping.Token);

        // The one thing a swarm provides that a test has to stand in for: an
        // address. Everything after it is this client talking to this client.
        using BittorrentEngine leeching = Engine("leech", new PointingTrackers(seeding.Port!.Value));

        leeching.Start();

        await leeching.AddAsync(
            new(file, ["http://tracker.example/announce"], Folder("leech"), torrent.TotalLength),
            stopping.Token);

        await Until(
            async () => (await leeching.StatusAsync(stopping.Token))[0].BytesDone == torrent.TotalLength,
            stopping.Token,
            async () => Said(await leeching.StatusAsync(stopping.Token)));

        TorrentStatus done = Assert.Single(await leeching.StatusAsync(stopping.Token));

        Assert.Equal(TorrentState.Seeding, done.State);
        Assert.Equal(torrent.TotalLength, done.BytesDone);

        // The bytes on disk. Nothing about this can pass by the two ends
        // agreeing with each other: the hashes were taken over the content
        // before either engine existed, and every piece was checked against
        // them before it was written.
        Assert.Equal(content, Downloaded(torrent, Folder("leech")));
    }

    /// <summary>A folder with the whole file in it, and a resume file that says so.</summary>
    /// <remarks>
    /// Written rather than downloaded, because what is being proved is the
    /// other end. A resume over files that really are the right length is how a
    /// client that has already finished starts up.
    /// </remarks>
    private static void Seeded(TorrentMetadata torrent, byte[] content, string folder)
    {
        using (TorrentDisk disk = new(torrent, folder))
        {
            disk.Create();

            for (int piece = 0; piece < torrent.PieceCount; piece++)
            {
                long at = piece * torrent.PieceLength;

                disk.Write(piece, content.AsSpan((int)at, (int)torrent.LengthOfPiece(piece)));
            }
        }

        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            everything.Set(piece);
        }

        new ResumeKeeper(folder, TimeSpan.Zero, TimeProvider.System).Stop(
        [
            new(
                torrent.InfoHash,
                everything,
                Uploaded: 0,
                Downloaded: torrent.TotalLength,
                [
                    .. torrent.Files.Select(one => new ResumeFile(
                        one.Path,
                        one.Length,
                        new FileInfo(Path.Combine(folder, one.Path.Replace('/', Path.DirectorySeparatorChar)))
                            .LastWriteTimeUtc)),
                ]),
        ]);
    }

    /// <summary>
    /// Seeding that does not stop, so the acceptance can look at a finished
    /// torrent. The rule itself is proved in RateAndChokeTests.
    /// </summary>
    private static readonly SeedLimit Seeding = new(Ratio: 0, For: TimeSpan.Zero);

    private BittorrentEngine Engine(string which, ITrackerTransport trackers)
    {
        return new(
            0,
            TimeSpan.FromMinutes(5),
            TimeSpan.FromMinutes(30),
            5,
            Seeding,
            0,
            0,
            null,
            new ActivityJournal(),
            new CapturingLogger(),
            trackers,
            new SocketPeerDialler(TimeSpan.FromSeconds(30)),
            resume: new ResumeKeeper(Folder(which), TimeSpan.FromSeconds(1), TimeProvider.System));
    }

    private string Folder(string which)
    {
        return Path.Combine(_root, which);
    }

    /// <summary>What came back off the disk, in the torrent's own order.</summary>
    private static byte[] Downloaded(TorrentMetadata torrent, string folder)
    {
        using TorrentDisk disk = new(torrent, folder);

        return disk.Read(0, (int)torrent.TotalLength);
    }

    /// <summary>What the page would be showing, so a failure says something.</summary>
    private static string Said(IReadOnlyList<TorrentStatus> status)
    {
        return string.Join(
            "; ",
            status.Select(one =>
                $"{one.State} {one.BytesDone}/{one.BytesTotal} peers {one.Peers} error {one.Error ?? "none"}"));
    }

    private static async Task Until(Func<Task<bool>> what, CancellationToken ct, Func<Task<string>> otherwise)
    {
        while (!await what())
        {
            if (ct.IsCancellationRequested)
            {
                Assert.Fail($"it never finished: {await otherwise()}");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }
    }

    /// <summary>The metadata for a torrent over these bytes.</summary>
    private static TorrentMetadata Synthetic(byte[] content, int pieceLength)
    {
        return TorrentMetadata.Read(Torrent(content, pieceLength));
    }

    /// <summary>A whole <c>.torrent</c> file over these bytes.</summary>
    private static byte[] Torrent(byte[] content, long pieceLength)
    {
        List<byte> hashes = [];

        for (int at = 0; at < content.Length; at += (int)pieceLength)
        {
            hashes.AddRange(SHA1.HashData(content.AsSpan(at, Math.Min((int)pieceLength, content.Length - at))));
        }

        return Bencode.Write(new BencodeDictionary(
        [
            new(
                "info"u8.ToArray(),
                new BencodeDictionary(
                [
                    new("length"u8.ToArray(), new BencodeInteger(content.Length)),
                    // A video, because this client downloads video files and nothing
                    // else — see docs/06-torrent-client.md § What is downloaded.
                    // Named .bin this torrent is refused before a byte of it is
                    // asked for, which is the rule working.
                    new("name"u8.ToArray(), new BencodeBytes("acceptance.mkv"u8.ToArray())),
                    new("piece length"u8.ToArray(), new BencodeInteger(pieceLength)),
                    new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),

                    // Private, because this client only ever uploads on a
                    // private torrent — see docs/06-torrent-client.md
                    // § Uploading. Over a public one the seeding end would
                    // refuse every request and this would be a test of the
                    // refusal rather than of the transfer.
                    new("private"u8.ToArray(), new BencodeInteger(1)),
                ])),
        ]));
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    /// <summary>A tracker that answers with one peer: the other engine.</summary>
    /// <remarks>
    /// A real bencoded announce with a compact peer list, which is what every
    /// tracker this client has been captured answering really sends.
    /// </remarks>
    private sealed class PointingTrackers(int port) : ITrackerTransport
    {
        public Task<byte[]> GetAsync(Uri address, CancellationToken ct)
        {
            byte[] peer = new byte[6];

            IPAddress.Loopback.GetAddressBytes().CopyTo(peer, 0);
            BinaryPrimitives.WriteUInt16BigEndian(peer.AsSpan(4), (ushort)port);

            return Task.FromResult(Bencode.Write(new BencodeDictionary(
            [
                new("interval"u8.ToArray(), new BencodeInteger(60)),
                new("peers"u8.ToArray(), new BencodeBytes(peer)),
            ])));
        }

        public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
        {
            throw new TimeoutException("this tracker only speaks HTTP");
        }
    }
}
