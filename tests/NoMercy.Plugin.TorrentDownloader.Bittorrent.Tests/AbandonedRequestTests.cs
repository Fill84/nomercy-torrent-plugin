using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// A piece asked of a peer that never answers is asked of somebody else.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is the fault that stopped downloads dead.</strong> A piece was
/// noted as being on its way the moment it was requested, and was only ever
/// forgotten when it arrived whole or failed its hash. A peer that took the
/// request and then went quiet — hung up, choked, throttled, simply gone — left
/// that piece marked as on its way for the rest of the run, and the picker
/// skips anything already on its way.
/// </para>
/// <para>
/// With a hundred peers coming and going the marked pieces pile up until every
/// piece still missing is marked, and then the picker has nothing to offer
/// anybody. On 22 August 2026 that is what the owner saw: <c>Silo S03E04</c> at
/// 24.5% of 3.6 GB, 111 peers, 95 of them seeds, and nought bytes a second
/// coming in while the client went on uploading to them.
/// </para>
/// <para>
/// The endgame hides it at the very end of a download — with a handful of
/// pieces left everything is asked of everybody — which is why this test leaves
/// more than that many stranded.
/// </para>
/// </remarks>
public class AbandonedRequestTests : IDisposable
{
    private const int PieceLength = 1024;

    private const int Pieces = 40;

    /// <summary>
    /// How long a piece may sit unanswered here.
    /// </summary>
    /// <remarks>
    /// Short, because the test waits it out in real time. What is being proved
    /// is that it is given back at all, not the length of the wait.
    /// </remarks>
    private static readonly TimeSpan Patience = TimeSpan.FromMilliseconds(250);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-abandoned-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task APieceAskedOfAPeerThatGoesQuietIsAskedOfSomebodyElse()
    {
        byte[] content = RandomNumberGenerator.GetBytes(PieceLength * Pieces);
        TorrentMetadata torrent = Torrent(content);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        using TorrentSession leecher = Fresh(torrent);

        // A peer with everything, that unchokes and then answers nothing at
        // all. Every nudge sends it after another piece, and every one of those
        // is a piece the picker will not offer anybody else again.
        (Stream quiet, Stream ours) = await LoopbackAsync(stopping.Token);

        PeerConnection?[] introduced = await Task.WhenAll(
            PeerConnection.IntroduceAsync(quiet, Hash(torrent), Id("QUIET"), Pieces, dialling: false, stopping.Token),
            PeerConnection.IntroduceAsync(ours, Hash(torrent), Id("LEECH"), Pieces, dialling: true, stopping.Token));

        PeerConnection silent = introduced[0]!;

        Task talking = leecher.RunAsync(introduced[1]!, stopping.Token);

        await silent.SendAsync(new(PeerMessageId.Bitfield, Everything(torrent).Write()), stopping.Token);
        await silent.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), stopping.Token);

        // Twenty of the forty, stranded. Twenty is more than the endgame's
        // handful, so nothing else in the client can rescue them.
        for (int nudge = 0; nudge < 20; nudge++)
        {
            await silent.SendAsync(PeerMessage.Have(nudge), stopping.Token);

            // Read what it asks for and answer none of it, which is the whole
            // behaviour being modelled.
            await silent.NextAsync(stopping.Token);
        }

        // A real seeder, arriving after the damage.
        (Stream seeding, Stream asking) = await LoopbackAsync(stopping.Token);

        using TorrentSession seeder = Seeding(torrent, content);

        PeerConnection?[] second = await Task.WhenAll(
            PeerConnection.IntroduceAsync(seeding, Hash(torrent), Id("SEED"), Pieces, dialling: false, stopping.Token),
            PeerConnection.IntroduceAsync(asking, Hash(torrent), Id("LEECH2"), Pieces, dialling: true, stopping.Token));

        Task serves = seeder.RunAsync(second[0]!, stopping.Token);
        Task asks = leecher.RunAsync(second[1]!, stopping.Token);

        await Task.WhenAny(Task.WhenAll(serves, asks), Finished(leecher, stopping.Token));

        await stopping.CancelAsync();

        await Task.WhenAll(
            talking.ContinueWith(_ => { }, TaskScheduler.Default),
            serves.ContinueWith(_ => { }, TaskScheduler.Default),
            asks.ContinueWith(_ => { }, TaskScheduler.Default));

        Assert.True(leecher.Complete, $"it stopped at {leecher.Progress().Verified} of {Pieces} pieces");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private TorrentSession Fresh(TorrentMetadata torrent)
    {
        string folder = Path.Combine(_folder, "leech");

        Directory.CreateDirectory(folder);

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        return new(torrent, disk, new(torrent.PieceCount), patience: Patience);
    }

    private TorrentSession Seeding(TorrentMetadata torrent, byte[] content)
    {
        string folder = Path.Combine(_folder, "seed");

        Directory.CreateDirectory(folder);

        TorrentDisk disk = new(torrent, folder);

        disk.Create();

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            disk.Write(piece, content.AsSpan(piece * PieceLength, (int)torrent.LengthOfPiece(piece)));
        }

        return new(torrent, disk, Everything(torrent));
    }

    private static Bitfield Everything(TorrentMetadata torrent)
    {
        Bitfield all = new(torrent.PieceCount);

        for (int piece = 0; piece < torrent.PieceCount; piece++)
        {
            all.Set(piece);
        }

        return all;
    }

    private static async Task Finished(TorrentSession session, CancellationToken ct)
    {
        while (!session.Complete && !ct.IsCancellationRequested)
        {
            await Task.Delay(25, ct);
        }
    }

    private static TorrentMetadata Torrent(byte[] content)
    {
        List<byte> hashes = [];

        for (int at = 0; at < content.Length; at += PieceLength)
        {
            hashes.AddRange(SHA1.HashData(content.AsSpan(at, Math.Min(PieceLength, content.Length - at))));
        }

        return TorrentMetadata.FromInfo(
            Bencode.Write(new BencodeDictionary(
            [
                new("length"u8.ToArray(), new BencodeInteger(content.Length)),
                new("name"u8.ToArray(), new BencodeBytes("stranded.bin"u8.ToArray())),
                new("piece length"u8.ToArray(), new BencodeInteger(PieceLength)),
                new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),

                // Private, because this client only uploads on a private
                // torrent — see docs/06-torrent-client.md § Uploading, and the
                // seeding side here is this client too.
                new("private"u8.ToArray(), new BencodeInteger(1)),
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
