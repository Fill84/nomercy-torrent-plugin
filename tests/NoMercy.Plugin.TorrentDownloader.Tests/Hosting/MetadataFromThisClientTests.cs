using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// One instance of this client answers another's magnet with the metadata, over a real socket.
/// </summary>
/// <remarks>
/// The search chain hands the client a hash and nothing else (<c>Grab</c>: "a hash is a magnet"), so a
/// download from a swarm of this client alone — the whole-run test of <c>S17-05</c>, and any owner seeding to
/// their own second machine — needs the seeding end to answer <c>ut_metadata</c> requests. Until this test the
/// client only ever asked them.
/// </remarks>
public sealed class MetadataFromThisClientTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-metadata-" + Guid.NewGuid().ToString("n")[..8]);

    [Fact]
    public async Task AClientWithAMagnetAloneGetsTheMetadataFromAClientThatHasTheTorrent()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        RealSwarm swarm = new(_root);
        byte[] content = RealSwarm.Fixture("archive-multifile.torrent");
        byte[] bytes = RealSwarm.Torrent(content, pieceLength: 2048);
        TorrentMetadata torrent = TorrentMetadata.Read(bytes);
        string file = Path.Combine(_root, "the.torrent");

        Directory.CreateDirectory(_root);
        File.WriteAllBytes(file, bytes);
        RealSwarm.Seeded(torrent, content, swarm.Folder("seed"));

        using BittorrentEngine seeding = swarm.Engine("seed", new SilentTrackers());

        seeding.Start();
        await seeding.AddAsync(new(file, [], swarm.Folder("seed")), stopping.Token);

        using BittorrentEngine leeching = swarm.Engine("leech", new RealSwarm.PointingTrackers(seeding.Port!.Value));

        leeching.Start();

        // The hash alone, as the search chain hands it over: no file list, no piece length, no name.
        await leeching.AddAsync(
            new($"magnet:?xt=urn:btih:{torrent.InfoHash}", ["http://tracker.example/announce"], swarm.Folder("leech")),
            stopping.Token);

        while (!stopping.IsCancellationRequested)
        {
            IReadOnlyList<TorrentStatus> status = await leeching.StatusAsync(CancellationToken.None);

            if (status.Count == 1 && status[0].BytesDone == torrent.TotalLength)
            {
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(100), CancellationToken.None);
        }

        TorrentStatus done = Assert.Single(await leeching.StatusAsync(CancellationToken.None));

        Assert.True(done.BytesTotal is long, $"the metadata never arrived from the seeding client; the leech stands at {done.State}");
        Assert.Equal(torrent.TotalLength, done.BytesDone);
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
