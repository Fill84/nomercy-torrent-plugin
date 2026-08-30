using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// How many pieces one peer is asked for at once.
/// </summary>
/// <remarks>
/// <para>
/// <strong>This is what put the media server at 45 GB of memory.</strong>
/// <c>Pipeline</c> is four, and its own summary says "how many pieces are asked
/// of one peer at a time" — but it was counted per call rather than per peer.
/// The asking runs on <em>every</em> message a peer sends, and every run
/// claimed up to four more pieces, each of which took a buffer the size of a
/// whole piece and was excluded from the picker until it arrived, failed, or
/// sat unanswered for a minute.
/// </para>
/// <para>
/// A peer that says anything at all without sending blocks — a run of
/// <c>have</c> from somebody downloading beside you is the ordinary case —
/// therefore walks the client through the entire file list, buffer by buffer.
/// On 30 August 2026 a 36.1 GB season pack did exactly that: forty-five
/// gigabytes resident, a torrent showing nought per cent, and the same machine
/// running the media server and its build runner.
/// </para>
/// </remarks>
public class PipelineDepthTests : IDisposable
{
    private const int PieceLength = 1024;

    /// <summary>
    /// Far more pieces than the pipeline is deep.
    /// </summary>
    /// <remarks>
    /// Forty, so that a client claiming its way through the torrent is nothing
    /// like a client claiming four — and well past the endgame, which hands
    /// every remaining piece to everybody once the end is in sight.
    /// </remarks>
    private const int Pieces = 40;

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-pipeline-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task OnePeerIsNeverAskedForMorePiecesThanThePipelineIsDeep()
    {
        byte[] content = RandomNumberGenerator.GetBytes(PieceLength * Pieces);
        TorrentMetadata torrent = Torrent(content);

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        using TorrentSession leecher = Fresh(torrent);

        (Stream theirs, Stream ours) = await LoopbackAsync(stopping.Token);

        PeerConnection?[] introduced = await Task.WhenAll(
            PeerConnection.IntroduceAsync(theirs, Hash(torrent), Id("QUIET"), Pieces, dialling: false, stopping.Token),
            PeerConnection.IntroduceAsync(ours, Hash(torrent), Id("LEECH"), Pieces, dialling: true, stopping.Token));

        PeerConnection peer = introduced[0]!;

        Task talking = leecher.RunAsync(introduced[1]!, stopping.Token);

        HashSet<int> asked = [];

        // Read for as long as the test lasts rather than a message at a time.
        // Nothing here answers a request, so a reader that stopped would leave
        // the socket to fill and the client would block on a write instead of
        // doing the thing being measured.
        Task reading = Task.Run(
            async () =>
            {
                while (!stopping.IsCancellationRequested)
                {
                    if (await peer.NextAsync(stopping.Token).ConfigureAwait(false) is not PeerMessage message)
                    {
                        // The other end has gone, which ends the reading and
                        // not the test: what it has already asked for stands.
                        return;
                    }

                    if (message.Id != PeerMessageId.Request)
                    {
                        continue;
                    }

                    (int piece, int _, int _) = message.AsRequest();

                    lock (asked)
                    {
                        asked.Add(piece);
                    }
                }
            },
            stopping.Token);

        await peer.SendAsync(new(PeerMessageId.Bitfield, Everything().Write()), stopping.Token);
        await peer.SendAsync(PeerMessage.Of(PeerMessageId.Unchoke), stopping.Token);

        // A peer that talks and sends nothing. Every one of these is a message
        // the client answers by asking for whatever it has not got, which is
        // the whole behaviour being measured.
        for (int nudge = 0; nudge < Pieces; nudge++)
        {
            await peer.SendAsync(PeerMessage.Have(nudge), stopping.Token);
        }

        // Long enough for every one of those to have been answered, and far
        // short of the minute a piece is given back after — a piece released
        // and claimed again is a different fault and would be counted here as
        // this one.
        await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token);

        int distinct;

        lock (asked)
        {
            distinct = asked.Count;
        }

        await stopping.CancelAsync();

        await Task.WhenAll(
            talking.ContinueWith(_ => { }, TaskScheduler.Default),
            reading.ContinueWith(_ => { }, TaskScheduler.Default));

        // It has to ask for something, or this proves nothing at all.
        Assert.InRange(distinct, 1, TorrentSession.Pipeline);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>A client with none of it, at the patience it really uses.</summary>
    private TorrentSession Fresh(TorrentMetadata torrent)
    {
        Directory.CreateDirectory(_folder);

        TorrentDisk disk = new(torrent, _folder);

        disk.Create();

        return new(torrent, disk, new(torrent.PieceCount));
    }

    private static Bitfield Everything()
    {
        Bitfield all = new(Pieces);

        for (int piece = 0; piece < Pieces; piece++)
        {
            all.Set(piece);
        }

        return all;
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
                new("name"u8.ToArray(), new BencodeBytes("pipeline.bin"u8.ToArray())),
                new("piece length"u8.ToArray(), new BencodeInteger(PieceLength)),
                new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),

                // Private, because this client only uploads on a private
                // torrent — see docs/06-torrent-client.md § Uploading.
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
