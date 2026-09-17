using NoMercy.Plugin.TorrentDownloader.Bittorrent;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// A download that really finished, between two real clients, and what happens to its files afterwards.
/// </summary>
/// <remarks>
/// Every fault the owner saw on 16 and 17 September 2026 after a download finished was in what held its files:
/// a copy that could not take the download away, a removed torrent whose files stayed "in use by another
/// process", a restart after which the files were written again. None of it shows with a client that is a fake.
/// </remarks>
public sealed class AFinishedDownloadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nomercy-finished-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// Let go of, a finished download's file can be moved at once — which is what staging does with it. The
    /// client kept every file of a torrent open for as long as it held it; staging used to copy beside that
    /// handle for thirty-five minutes and could not take the download away afterwards.
    /// </remarks>
    [Fact]
    public async Task AFinishedDownloadLetGoOfIsMovedAtOnce()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        (RealSwarm swarm, TorrentMetadata torrent, string file) = Swarm();

        using BittorrentEngine seeding = Seeder(swarm, file, stopping.Token);
        using BittorrentEngine leeching = swarm.Engine("leech", new RealSwarm.PointingTrackers(seeding.Port!.Value));

        leeching.Start();

        await leeching.AddAsync(new(file, ["http://tracker.example/announce"], swarm.Folder("leech")), stopping.Token);
        await FinishedAsync(leeching, torrent, stopping.Token);

        string downloaded = Path.Combine(swarm.Folder("leech"), torrent.Name);
        string intake = Path.Combine(_root, "intake", torrent.Name);

        Directory.CreateDirectory(Path.GetDirectoryName(intake)!);

        await leeching.ReleaseAsync(torrent.InfoHash, stopping.Token);

        File.Move(downloaded, intake);

        Assert.True(File.Exists(intake));
    }

    /// <remarks>
    /// <para>
    /// <strong>A restart finds what it finished and writes nothing to it.</strong> On 17 September 2026 the files
    /// of a finished South Park download carried the minute the server came back up as the time they were
    /// written. A file written after its download finished is one whose resume record no longer matches, so its
    /// pieces are no longer trusted. Here a restart with no peer to ask finds the download whole and leaves the
    /// file as it was.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARestartFindsAFinishedDownloadWithoutWritingToIt()
    {
        using CancellationTokenSource stopping = new(Hang.Limit);
        (RealSwarm swarm, TorrentMetadata torrent, string file) = Swarm();

        using BittorrentEngine seeding = Seeder(swarm, file, stopping.Token);

        string downloaded = Path.Combine(swarm.Folder("leech"), torrent.Name);

        using (BittorrentEngine first = swarm.Engine("leech", new RealSwarm.PointingTrackers(seeding.Port!.Value)))
        {
            first.Start();

            await first.AddAsync(new(file, ["http://tracker.example/announce"], swarm.Folder("leech")), stopping.Token);
            await FinishedAsync(first, torrent, stopping.Token);
        }

        DateTime written = File.GetLastWriteTimeUtc(downloaded);

        // Past the resolution of any file system's clock, so a write after the restart cannot carry the same time.
        await Task.Delay(TimeSpan.FromSeconds(2), stopping.Token);

        // No tracker, so no peer: whole again can only be what was already on disk and trusted.
        using BittorrentEngine again = swarm.Engine("leech", new SilentTrackers());

        again.Start();

        await again.AddAsync(new(file, [], swarm.Folder("leech")), stopping.Token);
        await FinishedAsync(again, torrent, stopping.Token);

        Assert.Equal(written, File.GetLastWriteTimeUtc(downloaded));
    }

    private (RealSwarm Swarm, TorrentMetadata Torrent, string File) Swarm()
    {
        RealSwarm swarm = new(_root);
        byte[] content = RealSwarm.Fixture("archive-multifile.torrent");
        byte[] bytes = RealSwarm.Torrent(content, pieceLength: 2048);
        TorrentMetadata torrent = TorrentMetadata.Read(bytes);
        string file = Path.Combine(_root, "the.torrent");

        Directory.CreateDirectory(_root);
        File.WriteAllBytes(file, bytes);

        RealSwarm.Seeded(torrent, content, swarm.Folder("seed"));

        return (swarm, torrent, file);
    }

    private static BittorrentEngine Seeder(RealSwarm swarm, string file, CancellationToken ct)
    {
        BittorrentEngine seeding = swarm.Engine("seed", new SilentTrackers());

        seeding.Start();
        seeding.AddAsync(new(file, [], swarm.Folder("seed")), ct).GetAwaiter().GetResult();

        return seeding;
    }

    private static async Task FinishedAsync(BittorrentEngine engine, TorrentMetadata torrent, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            IReadOnlyList<TorrentStatus> status = await engine.StatusAsync(CancellationToken.None);

            if (status.Count == 1 && status[0].BytesDone == torrent.TotalLength)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), CancellationToken.None);
        }

        Assert.Fail("the download never finished.");
    }

    public void Dispose()
    {
        TemporaryFolder.Forget(_root);
    }
}
