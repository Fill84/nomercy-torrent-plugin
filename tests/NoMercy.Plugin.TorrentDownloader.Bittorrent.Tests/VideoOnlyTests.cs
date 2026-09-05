using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Only the files that were asked for are downloaded.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The owner's rule: video files and nothing else.</strong> A torrent
/// is a bag somebody else packed, and what is in it besides the episode is
/// their idea rather than the owner's. On 22 August 2026 a 1.2 GB file called
/// <c>Lioness 2023 S03E02 1080p WEB h264-ETHEL.exe</c> downloaded to completion
/// on the owner's server, because the only thing that knew what a video file
/// was ran after the download instead of before it.
/// </para>
/// <para>
/// The session is told which pieces it wants and asks for nothing else. What is
/// proved here is the outcome on disk: the wanted file arrives byte for byte,
/// and not one byte of the other is ever written.
/// </para>
/// </remarks>
public class VideoOnlyTests : IDisposable
{
    private const int PieceLength = 2048;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-videoonly-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task NothingOutsideTheWantedFilesIsEverAskedFor()
    {
        // Both a whole number of pieces long, so no piece straddles the two: at
        // BitTorrent's granularity a piece is either wanted or it is not, and a
        // boundary piece carries a fragment of its neighbour however carefully
        // it is asked for.
        byte[] episode = RandomNumberGenerator.GetBytes(PieceLength * 6);
        byte[] installer = RandomNumberGenerator.GetBytes(PieceLength * 3);

        TorrentMetadata torrent = TwoFiles(episode, installer);

        Assert.Equal(9, torrent.PieceCount);

        // The mask: the pieces the episode lives in, and no others.
        Bitfield wanted = torrent.PiecesOf([torrent.Files[0]]);

        Assert.Equal(6, wanted.Count);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        (Stream seeding, Stream leeching) = await LoopbackAsync(stopping.Token);

        using TorrentSession seeder = Seeding(torrent, [.. episode, .. installer]);
        using TorrentSession leecher = Fresh(torrent, wanted);

        Assert.False(leecher.Complete);

        Task<PeerConnection?> serving = PeerConnection.IntroduceAsync(
            seeding,
            Convert.FromHexString(torrent.InfoHash),
            Id("SEED"),
            torrent.PieceCount,
            dialling: false,
            stopping.Token);

        Task<PeerConnection?> asking = PeerConnection.IntroduceAsync(
            leeching,
            Convert.FromHexString(torrent.InfoHash),
            Id("LEECH"),
            torrent.PieceCount,
            dialling: true,
            stopping.Token);

        PeerConnection?[] both = await Task.WhenAll(serving, asking);

        Task serves = seeder.RunAsync(both[0]!, stopping.Token);
        Task asks = leecher.RunAsync(both[1]!, stopping.Token);

        await Task.WhenAny(Task.WhenAll(serves, asks), Finished(leecher, stopping.Token));

        await stopping.CancelAsync();
        await Task.WhenAll(
            serves.ContinueWith(_ => { }, TaskScheduler.Default),
            asks.ContinueWith(_ => { }, TaskScheduler.Default));

        // Finished, on a torrent it has six of the nine pieces of. Complete has
        // to mean "everything wanted" or a download of part of a torrent never
        // ends and never stages.
        Assert.True(leecher.Complete);

        Assert.Equal(episode, OnDisk(torrent, torrent.Files[0]));

        // And not one byte of the other file. Its pieces were never asked for,
        // so what is on disk is the length it was created at with nothing
        // written into it.
        Assert.All(OnDisk(torrent, torrent.Files[1]), written => Assert.Equal(0, written));
    }

    public void Dispose()
    {
        TempFolder.Clear(_folder);

        GC.SuppressFinalize(this);
    }

    private TorrentSession Seeding(TorrentMetadata torrent, byte[] content)
    {
        string folder = Path.Combine(_folder, "seed");

        Directory.CreateDirectory(folder);

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        Bitfield everything = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            disk.Write(piece, content.AsSpan(piece * PieceLength, (int)torrent.LengthOfPiece(piece)));
            everything.Set(piece);
        }

        return new(torrent, disk, everything);
    }

    private TorrentSession Fresh(TorrentMetadata torrent, Bitfield wanted)
    {
        string folder = Path.Combine(_folder, "leech");

        Directory.CreateDirectory(folder);

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        return new(torrent, disk, new(torrent.PieceCount), wanted);
    }

    private byte[] OnDisk(TorrentMetadata torrent, TorrentFileEntry file)
    {
        using TorrentDisk disk = new(torrent, Path.Combine(_folder, "leech"));

        // Shared, because the session that wrote it is still holding it open:
        // it is asserted while it is running, which is the state that matters.
        using FileStream open = new(disk.PathOf(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        byte[] bytes = new byte[open.Length];

        open.ReadExactly(bytes);

        return bytes;
    }

    private static async Task Finished(TorrentSession session, CancellationToken ct)
    {
        while (!session.Complete && !ct.IsCancellationRequested)
        {
            await Task.Delay(25, ct);
        }
    }

    /// <summary>A two-file torrent: the episode, then something else.</summary>
    private static TorrentMetadata TwoFiles(byte[] first, byte[] second)
    {
        byte[] whole = [.. first, .. second];
        List<byte> hashes = [];

        for (int at = 0; at < whole.Length; at += PieceLength)
        {
            hashes.AddRange(SHA1.HashData(whole.AsSpan(at, Math.Min(PieceLength, whole.Length - at))));
        }

        return TorrentMetadata.FromInfo(
            Bencode.Write(new BencodeDictionary(
            [
                new(
                    "files"u8.ToArray(),
                    new BencodeList(
                    [
                        Entry("Lioness.S03E02.1080p.WEB.h264-ETHEL.mkv", first.Length),
                        Entry("Lioness.S03E02.1080p.WEB.h264-ETHEL.exe", second.Length),
                    ])),
                new("name"u8.ToArray(), new BencodeBytes("Lioness.S03E02"u8.ToArray())),
                new("piece length"u8.ToArray(), new BencodeInteger(PieceLength)),
                new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),

                // Private, because this client only uploads on a private
                // torrent — see docs/06-torrent-client.md § Uploading. The
                // seeding side of this test is this client too.
                new("private"u8.ToArray(), new BencodeInteger(1)),
            ])),
            []);
    }

    private static BencodeDictionary Entry(string name, int length)
    {
        return new(
        [
            new("length"u8.ToArray(), new BencodeInteger(length)),
            new("path"u8.ToArray(), new BencodeList([new BencodeBytes(Encoding.UTF8.GetBytes(name))])),
        ]);
    }

    private static byte[] Id(string what)
    {
        return Encoding.ASCII.GetBytes(("-NM0400-" + what).PadRight(20, '0')[..20]);
    }

    private static async Task<(Stream Server, Stream Client)> LoopbackAsync(CancellationToken ct)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        try
        {
            TcpClient dialling = new();

            Task connecting = dialling
                .ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct)
                .AsTask();

            TcpClient accepted = await listener.AcceptTcpClientAsync(ct);

            await connecting;

            return (accepted.GetStream(), dialling.GetStream());
        }
        finally
        {
            listener.Stop();
        }
    }
}
