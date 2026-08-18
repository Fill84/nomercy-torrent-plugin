using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The client, joined up: one instance of it downloading a torrent from another.
/// </summary>
/// <remarks>
/// <para>
/// Everything Sprint 5 built runs here at once — the handshake, the bitfield,
/// interested and unchoke, requests and blocks, SHA-1 verification, the disk,
/// the picker — and the proof is a file on disk that is byte for byte the file
/// that was seeded.
/// </para>
/// <para>
/// Over a real TCP connection on this machine, because that is the only kind
/// available: no peer in a public swarm will accept a connection from here, and
/// nothing on this network will map a port so that one could dial in. A second
/// instance of this client is the peer, which is what `S5-13` asks for.
/// </para>
/// <para>
/// The torrent is made from a real file — the captured Ubuntu <c>.torrent</c>,
/// 484 kilobytes of it — because what matters is that the bytes are somebody
/// else's and that the piece hashes are computed over them rather than agreed
/// between the two ends.
/// </para>
/// </remarks>
public class TorrentSessionTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-session-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The whole of it. One session seeds a file it has; the other starts with
    /// an empty folder and finishes with the same bytes, every piece of them
    /// hashed against what the torrent said before any of it reached the disk.
    /// </remarks>
    [Fact]
    public async Task OneInstanceOfThisClientDownloadsAWholeTorrentFromAnother()
    {
        byte[] content = Fixture("ubuntu-desktop.torrent");
        TorrentMetadata torrent = TorrentOf(content, pieceLength: 32768);

        Assert.InRange(torrent.PieceCount, 10, 200);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        (Stream seeding, Stream leeching) = await LoopbackAsync(stopping.Token);

        using TorrentSession seeder = Seeding(torrent, content);
        using TorrentSession leecher = Fresh(torrent);

        Assert.True(seeder.Complete);
        Assert.False(leecher.Complete);

        // Both at once. The side that answers reads before it writes, so
        // waiting for it before the dialling side has said anything is a
        // deadlock — which is exactly what happened the first time this was
        // written, and is what a real client would do to itself if it accepted
        // connections on one thread.
        Task<PeerConnection?> serving = PeerConnection.IntroduceAsync(
            seeding, Hash(torrent), Id("SEED"), torrent.PieceCount, dialling: false, stopping.Token);

        Task<PeerConnection?> asking = PeerConnection.IntroduceAsync(
            leeching, Hash(torrent), Id("LEECH"), torrent.PieceCount, dialling: true, stopping.Token);

        PeerConnection?[] both = await Task.WhenAll(serving, asking);

        Assert.NotNull(both[0]);
        Assert.NotNull(both[1]);

        Task serves = seeder.RunAsync(both[0]!, stopping.Token);
        Task asks = leecher.RunAsync(both[1]!, stopping.Token);

        await Task.WhenAny(Task.WhenAll(serves, asks), Finished(leecher, stopping.Token));

        Assert.True(leecher.Complete, "the torrent did not finish");

        // Both sides carry on seeding once they are complete, so it takes
        // saying so to stop them.
        await stopping.CancelAsync();

        await Task.WhenAll(
            serves.ContinueWith(_ => { }, TaskScheduler.Default),
            asks.ContinueWith(_ => { }, TaskScheduler.Default));

        // The file on disk, byte for byte. Nothing about this can pass by the
        // two ends agreeing with each other: the hashes were taken over the
        // bytes before either session existed.
        Assert.Equal(content, Downloaded(torrent));

        SessionProgress progress = leecher.Progress();

        Assert.Equal(torrent.PieceCount, progress.Verified);
        Assert.Equal(content.Length, progress.BytesDone);
        Assert.InRange(progress.Downloaded, content.Length, long.MaxValue);
    }

    /// <remarks>
    /// While it runs, the numbers are real ones. 0.3.4 showed "0 downloads"
    /// while two were running, and every number on this record is measured or
    /// says it is not known.
    /// </remarks>
    [Fact]
    public void ProgressIsCountedFromWhatIsVerifiedAndNotFromWhatArrived()
    {
        byte[] content = Fixture("ubuntu-desktop.torrent");
        TorrentMetadata torrent = TorrentOf(content, pieceLength: 32768);

        using TorrentSession fresh = Fresh(torrent);

        SessionProgress nothing = fresh.Progress();

        Assert.Equal(0, nothing.Verified);
        Assert.Equal(0, nothing.BytesDone);
        Assert.Equal(0, nothing.Peers);
        Assert.False(nothing.Complete);

        using TorrentSession seeding = Seeding(torrent, content, "seed-progress");

        SessionProgress everything = seeding.Progress();

        Assert.Equal(torrent.PieceCount, everything.Verified);
        Assert.Equal(content.Length, everything.BytesDone);
        Assert.True(everything.Complete);
    }

    /// <remarks>
    /// <para>
    /// A peer that answers with rubbish. The piece fails its hash, and
    /// <strong>nothing of it reaches the disk</strong> — which is the whole
    /// reason a piece is verified before it is written rather than after.
    /// </para>
    /// <para>
    /// It is not a hypothetical peer. A swarm contains clients that are broken
    /// and clients that are hostile, and the only thing that tells them from a
    /// good one is the twenty bytes the torrent named.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task APeerThatSendsRubbishHasNoneOfItWrittenToDisk()
    {
        byte[] content = Fixture("ubuntu-desktop.torrent");
        TorrentMetadata torrent = TorrentOf(content, pieceLength: 32768);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));

        (Stream lying, Stream leeching) = await LoopbackAsync(stopping.Token);

        using TorrentSession leecher = Fresh(torrent);

        Task<PeerConnection?> theirs = PeerConnection.IntroduceAsync(
            lying, Hash(torrent), Id("LIAR"), torrent.PieceCount, dialling: false, stopping.Token);

        Task<PeerConnection?> ours = PeerConnection.IntroduceAsync(
            leeching, Hash(torrent), Id("LEECH"), torrent.PieceCount, dialling: true, stopping.Token);

        PeerConnection?[] both = await Task.WhenAll(theirs, ours);

        Task asks = leecher.RunAsync(both[1]!, stopping.Token);

        // A peer claiming everything, and answering every request with nought
        // bytes — the right length, the wrong contents, which is the case a
        // length check alone would never catch.
        PeerConnection liar = both[0]!;
        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            everything.Set(piece);
        }

        await liar.SendAsync(new(PeerMessageId.Bitfield, everything.Write()), stopping.Token);
        await liar.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), stopping.Token);

        for (int answered = 0; answered < 8 && !stopping.IsCancellationRequested; answered++)
        {
            PeerMessage? asked = await liar.NextAsync(stopping.Token);

            if (asked?.Id != PeerMessageId.Request)
            {
                continue;
            }

            (int piece, int offset, int length) = asked.AsRequest();

            await liar.SendAsync(PeerMessage.Block(piece, offset, new byte[length]), stopping.Token);
        }

        await stopping.CancelAsync();
        await asks.ContinueWith(_ => { }, TaskScheduler.Default);

        // Not one piece verified, and the file is still the nothing it was
        // created as: every byte of what that peer sent was thrown away.
        Assert.Equal(0, leecher.Progress().Verified);
        Assert.All(Downloaded(torrent), one => Assert.Equal(0, one));
        Assert.InRange(leecher.Progress().Downloaded, 1, long.MaxValue);
    }

    /// <remarks>
    /// A peer that shakes hands about another torrent is a different swarm.
    /// BEP 3 says to drop it, and writing its blocks into these files would be
    /// writing somebody else's bytes into the owner's library.
    /// </remarks>
    [Fact]
    public async Task APeerThatOffersAnotherTorrentIsDropped()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(10));

        (Stream theirs, Stream ours) = await LoopbackAsync(stopping.Token);

        byte[] wrong = RandomNumberGenerator.GetBytes(20);

        await theirs.WriteAsync(Handshake.Write(wrong, Id("OTHER")), stopping.Token);
        await theirs.FlushAsync(stopping.Token);

        Assert.Null(await PeerConnection.IntroduceAsync(
            ours, RandomNumberGenerator.GetBytes(20), Id("US"), 10, dialling: true, stopping.Token));
    }

    /// <remarks>
    /// A peer that hangs up without saying anything is the ordinary case out
    /// there, not a fault: almost every peer this client has ever dialled did
    /// exactly that.
    /// </remarks>
    [Fact]
    public async Task APeerThatHangsUpWithoutSpeakingIsNotAFault()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(10));

        (Stream theirs, Stream ours) = await LoopbackAsync(stopping.Token);

        theirs.Dispose();

        Assert.Null(await PeerConnection.IntroduceAsync(
            ours, RandomNumberGenerator.GetBytes(20), Id("US"), 10, dialling: true, stopping.Token));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A session with the whole file already on disk.</summary>
    private TorrentSession Seeding(TorrentMetadata torrent, byte[] content, string where = "seed")
    {
        string folder = Path.Combine(_folder, where);

        Directory.CreateDirectory(folder);
        File.WriteAllBytes(Path.Combine(folder, torrent.Name), content);

        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            everything.Set(piece);
        }

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        return new(torrent, disk, everything);
    }

    /// <summary>A session with nothing at all.</summary>
    private TorrentSession Fresh(TorrentMetadata torrent)
    {
        string folder = Path.Combine(_folder, "leech");

        Directory.CreateDirectory(folder);

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        return new(torrent, disk, new(torrent.PieceCount));
    }

    /// <summary>What the downloading side ended up with.</summary>
    private byte[] Downloaded(TorrentMetadata torrent)
    {
        using FileStream file = new(
            Path.Combine(_folder, "leech", torrent.Name),
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);

        byte[] bytes = new byte[file.Length];

        file.ReadExactly(bytes);

        return bytes;
    }

    /// <summary>Waits for the download rather than for the connections to close.</summary>
    private static async Task Finished(TorrentSession session, CancellationToken ct)
    {
        while (!session.Complete && !ct.IsCancellationRequested)
        {
            await Task.Delay(25, ct);
        }
    }

    /// <summary>
    /// Two ends of a real TCP connection on this machine.
    /// </summary>
    /// <remarks>
    /// Loopback rather than a pipe, because the thing being tested is a client
    /// talking to a peer over a socket — with the reads arriving in whatever
    /// sizes the stack feels like, which is exactly what the message reader is
    /// there to survive.
    /// </remarks>
    private static async Task<(Stream Server, Stream Client)> LoopbackAsync(CancellationToken ct)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            TcpClient dialling = new();

            Task connecting = dialling.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct).AsTask();
            TcpClient accepted = await listener.AcceptTcpClientAsync(ct);

            await connecting;

            return (accepted.GetStream(), dialling.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>
    /// A torrent for these bytes, with the piece hashes taken over them.
    /// </summary>
    /// <remarks>
    /// Built through this client's own bencode writer and read back through
    /// <c>FromInfo</c>, which is how a magnet's metadata arrives. What the
    /// reader makes of a real published torrent is asserted elsewhere; what
    /// matters here is that the hashes are real hashes of real bytes.
    /// </remarks>
    private static TorrentMetadata TorrentOf(byte[] content, int pieceLength)
    {
        List<byte> hashes = [];

        for (int at = 0; at < content.Length; at += pieceLength)
        {
            hashes.AddRange(SHA1.HashData(content.AsSpan(at, Math.Min(pieceLength, content.Length - at))));
        }

        return TorrentMetadata.FromInfo(
            Bencode.Write(new BencodeDictionary(
            [
                new("length"u8.ToArray(), new BencodeInteger(content.Length)),
                new("name"u8.ToArray(), new BencodeBytes("seeded.bin"u8.ToArray())),
                new("piece length"u8.ToArray(), new BencodeInteger(pieceLength)),
                new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),
            ])),
            []);
    }

    private static byte[] Hash(TorrentMetadata torrent)
    {
        return Convert.FromHexString(torrent.InfoHash);
    }

    private static byte[] Id(string what)
    {
        return Encoding.ASCII.GetBytes(("-NM0400-" + what).PadRight(20, '0')[..20]);
    }

    private static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "tests", "fixtures")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }
}
