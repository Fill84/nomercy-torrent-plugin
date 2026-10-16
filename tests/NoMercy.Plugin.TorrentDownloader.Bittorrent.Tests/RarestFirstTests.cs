using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The rarest pieces are asked for first, rarest across every peer connected.
/// </summary>
/// <remarks>
/// <para>
/// <c>S14-04</c> found the session counting a piece's availability from one peer at a time: each message added
/// that peer's bitfield to the picker, picked, and took it out again, so every piece the peer had was as rare
/// as every other, and a peer that was still choking was never counted at all. Rarest-first then picked at
/// random — the pieces only one peer holds went with that peer, and the download ended waiting on them.
/// </para>
/// <para>
/// Two peers: one has pieces 0 to 35, the other every piece. Only the second unchokes. Pieces 36 to 39 are
/// held by one peer and every other piece by two, so what the second is asked for first is those four. Counted
/// one peer at a time, all thirty-six it could be asked for look the same.
/// </para>
/// </remarks>
public sealed class RarestFirstTests : IDisposable
{
    private const int PieceLength = 1024;

    private const int Pieces = 40;

    /// <summary>The pieces only the peer with everything holds.</summary>
    private static readonly int[] OnlyOneHas = [36, 37, 38, 39];

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "nomercy-rarest-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task WhatIsAskedForFirstIsWhatTheFewestPeersHave()
    {
        byte[] content = RandomNumberGenerator.GetBytes(PieceLength * Pieces);
        TorrentMetadata torrent = Torrent(content);

        using CancellationTokenSource stopping = new(Hang.Limit);

        // Four already verified, so rarest-first is in force: the first four are picked at random on purpose.
        Bitfield verified = new(Pieces);

        for (int piece = 0; piece < PiecePicker.RandomFirst; piece++)
        {
            verified.Set(piece);
        }

        Directory.CreateDirectory(_folder);
        TorrentDisk disk = new(torrent, _folder);
        disk.Create();

        using TorrentSession leecher = new(torrent, disk, verified);

        List<Task> talking = [];

        // The peer with most of it, which never unchokes.
        PeerConnection common = await ConnectAsync(leecher, torrent, "COMMON", talking, stopping.Token);
        Bitfield most = new(Pieces);

        for (int piece = 0; piece < Pieces - OnlyOneHas.Length; piece++)
        {
            most.Set(piece);
        }

        await common.SendAsync(new(PeerMessageId.Bitfield, most.Write()), stopping.Token);
        await UntilAsync(() => leecher.Holding().Unheld == OnlyOneHas.Length, stopping.Token);

        // The peer with everything, which unchokes once its bitfield is in.
        PeerConnection whole = await ConnectAsync(leecher, torrent, "WHOLE", talking, stopping.Token);
        Bitfield all = new(Pieces);

        for (int piece = 0; piece < Pieces; piece++)
        {
            all.Set(piece);
        }

        await whole.SendAsync(new(PeerMessageId.Bitfield, all.Write()), stopping.Token);
        await UntilAsync(() => leecher.Holding().Unheld == 0, stopping.Token);
        await whole.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), stopping.Token);

        HashSet<int> asked = [];

        while (asked.Count < OnlyOneHas.Length)
        {
            PeerMessage? message = await whole.NextAsync(stopping.Token);

            Assert.NotNull(message);

            if (message.Id == PeerMessageId.Request)
            {
                asked.Add(message.AsRequest().Piece);
            }
        }

        Assert.Equal(OnlyOneHas, asked.Order());

        await stopping.CancelAsync();
        await Task.WhenAll(talking).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception held) when (held is IOException or UnauthorizedAccessException)
        {
            // Still open; the temporary directory is the operating system's to clear.
        }
    }

    private static async Task<PeerConnection> ConnectAsync(
        TorrentSession leecher,
        TorrentMetadata torrent,
        string name,
        List<Task> talking,
        CancellationToken ct)
    {
        (Stream theirs, Stream ours) = await LoopbackAsync(ct);

        PeerConnection?[] introduced = await Task.WhenAll(
            PeerConnection.IntroduceAsync(theirs, Hash(torrent), Id(name), Pieces, dialling: false, ct),
            PeerConnection.IntroduceAsync(ours, Hash(torrent), Id("LEECH" + name), Pieces, dialling: true, ct));

        talking.Add(leecher.RunAsync(introduced[1]!, ct));

        return introduced[0]!;
    }

    private static async Task UntilAsync(Func<bool> ready, CancellationToken ct)
    {
        while (!ready())
        {
            await Task.Delay(10, ct);
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
                new("name"u8.ToArray(), new BencodeBytes("rarest.bin"u8.ToArray())),
                new("piece length"u8.ToArray(), new BencodeInteger(PieceLength)),
                new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),
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
