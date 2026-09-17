using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Hosting;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Two real clients and a torrent between them: one that has it, one that downloads it over a socket.
/// </summary>
/// <remarks>
/// What a fault in file handles, resume files or staging needs to be seen at all. The fakes stand in for
/// the client, and a fake holds no file open: the South Park release of 17 September 2026 stayed "in use by
/// another process" after its torrent was removed, and no test with a fake could have shown it.
/// </remarks>
public sealed class RealSwarm(string root)
{
    /// <summary>Seeding that does not stop, so a finished torrent can be looked at.</summary>
    public static readonly SeedLimit Seeding = new(Ratio: 0, For: TimeSpan.Zero);

    /// <summary>Where one end keeps its downloads and its resume files.</summary>
    public string Folder(string which)
    {
        return Path.Combine(root, which);
    }

    /// <summary>A client, with its resume files beside its downloads as the plugin runs it.</summary>
    public BittorrentEngine Engine(string which, ITrackerTransport trackers, CapturingLogger? log = null)
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
            log ?? new CapturingLogger(),
            trackers,
            new SocketPeerDialler(TimeSpan.FromSeconds(30)),
            resume: new ResumeKeeper(Folder(which), TimeSpan.FromSeconds(1), TimeProvider.System));
    }

    /// <summary>A folder with the whole file in it, and a resume file that says so.</summary>
    public static void Seeded(TorrentMetadata torrent, byte[] content, string folder)
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

    /// <summary>A whole single-file <c>.torrent</c> over these bytes, named as a video.</summary>
    /// <remarks>
    /// Private, because this client only uploads on a private torrent; over a public one the seeding end
    /// would refuse every request. A video, because only video files are downloaded.
    /// </remarks>
    public static byte[] Torrent(byte[] content, long pieceLength, string name = "acceptance.mkv")
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
                    new("name"u8.ToArray(), new BencodeBytes(System.Text.Encoding.UTF8.GetBytes(name))),
                    new("piece length"u8.ToArray(), new BencodeInteger(pieceLength)),
                    new("pieces"u8.ToArray(), new BencodeBytes([.. hashes])),
                    new("private"u8.ToArray(), new BencodeInteger(1)),
                ])),
        ]));
    }

    /// <summary>The bytes of a captured fixture.</summary>
    public static byte[] Fixture(string name)
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "NoMercy.Plugin.TorrentDownloader.sln")))
        {
            directory = directory.Parent;
        }

        return File.ReadAllBytes(Path.Combine(directory!.FullName, "tests", "fixtures", name));
    }

    /// <summary>A tracker that answers with one peer: the other client.</summary>
    public sealed class PointingTrackers(int port) : ITrackerTransport
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
