using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Moving a finished download into the intake folder, on a real disk.
/// </summary>
/// <remarks>
/// Real files, because every failure worth catching here is a file-system one:
/// a folder that cannot be written to, a copy that came out the wrong length, a
/// download that must still be there afterwards.
/// </remarks>
public class StagerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-staging-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The file arrives in the intake folder under its own name, whole, and is
    /// gone from the download folder afterwards.
    /// </remarks>
    [Fact]
    public async Task AStagedFileArrivesWholeAndLeavesTheDownloadFolder()
    {
        byte[] content = Content(3 * 1024 * 1024);
        string from = Folder("incomplete");
        string into = Path.Combine(_root, "intake");

        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03E06.mkv"), content);

        ActivityJournal journal = new();

        IReadOnlyList<StagedResult> results = await new Stager(journal, new CapturingLogger()).MoveAsync(
            [new("Silo.S03E06.mkv", Episode(6), content.Length)],
            from,
            into,
            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None);

        StagedResult result = Assert.Single(results);

        Assert.True(result.Moved);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(into, "Silo.S03E06.mkv")));
        Assert.False(File.Exists(Path.Combine(from, "Silo.S03E06.mkv")));

        Assert.Contains(journal.Snapshot().History, one => one.Outcome == ActivityOutcome.Finished);
    }

    /// <remarks>
    /// A file inside the torrent's own folders arrives flat in the intake
    /// folder: the encoder takes a path and has no interest in the folders a
    /// torrent came in.
    /// </remarks>
    [Fact]
    public async Task AFileInsideTheTorrentsOwnFoldersArrivesFlat()
    {
        string from = Folder("incomplete");
        string into = Path.Combine(_root, "intake");

        Directory.CreateDirectory(Path.Combine(from, "Silo.S03"));
        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03", "Silo.S03E01.mkv"), Content(1024));

        await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [new("Silo.S03/Silo.S03E01.mkv", Episode(1), 1024)],
            from,
            into,
            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(into, "Silo.S03E01.mkv")));
    }

    /// <remarks>
    /// <para>
    /// Staging into a folder that cannot be written fails loudly — in the log
    /// and in the journal, with the reason — and <strong>leaves the download
    /// exactly where it was</strong>.
    /// </para>
    /// <para>
    /// That second half is the one that matters. An unwritable intake folder is
    /// something the owner has to fix, and deleting the only copy of the
    /// episode while saying so would be unforgivable.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StagingIntoAFolderThatCannotBeWrittenFailsLoudlyAndKeepsTheDownload()
    {
        string from = Folder("incomplete");
        byte[] content = Content(4096);

        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03E06.mkv"), content);

        // A file where the intake folder should be: creating a directory over
        // it is refused by every file system there is.
        string into = Path.Combine(_root, "intake-is-a-file");

        await File.WriteAllTextAsync(into, "not a folder");

        ActivityJournal journal = new();
        CapturingLogger log = new();

        StagedResult result = Assert.Single(await new Stager(journal, log).MoveAsync(
            [new("Silo.S03E06.mkv", Episode(6), content.Length)],
            from,
            into,

            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None));

        Assert.False(result.Moved);
        Assert.NotNull(result.Reason);
        Assert.Contains("Silo.S03E06.mkv", result.Reason!, StringComparison.Ordinal);

        // The download is untouched, which is the whole point.
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(from, "Silo.S03E06.mkv")));

        Assert.Contains(journal.Snapshot().History, one => one.Outcome == ActivityOutcome.Failed);
        Assert.NotEmpty(log.Lines);
    }

    /// <remarks>
    /// A copy that came out the wrong length is thrown away and the download is
    /// kept. On some file systems running out of disk half way through gives a
    /// short file and no exception at all.
    /// </remarks>
    [Fact]
    public async Task ACopyThatCameOutTheWrongLengthIsNotAcceptedAsStaged()
    {
        string from = Folder("incomplete");
        string into = Path.Combine(_root, "intake");

        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03E06.mkv"), Content(4096));

        // The staging decision says it is bigger than it is, which is what a
        // truncated copy looks like from here.
        StagedResult result = Assert.Single(await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [new("Silo.S03E06.mkv", Episode(6), 8192)],
            from,
            into,

            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None));

        Assert.False(result.Moved);
        Assert.True(File.Exists(Path.Combine(from, "Silo.S03E06.mkv")));
        Assert.False(File.Exists(Path.Combine(into, "Silo.S03E06.mkv")));
    }

    /// <remarks>
    /// Several files, and one that fails does not stop the others: a season
    /// pack with one bad episode still stages the rest.
    /// </remarks>
    [Fact]
    public async Task OneFileFailingDoesNotStopTheRest()
    {
        string from = Folder("incomplete");
        string into = Path.Combine(_root, "intake");

        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03E01.mkv"), Content(2048));
        await File.WriteAllBytesAsync(Path.Combine(from, "Silo.S03E03.mkv"), Content(2048));

        IReadOnlyList<StagedResult> results = await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [
                new("Silo.S03E01.mkv", Episode(1), 2048),
                new("Silo.S03E02.mkv", Episode(2), 2048),
                new("Silo.S03E03.mkv", Episode(3), 2048),
            ],
            from,
            into,
            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None);

        Assert.Equal([true, false, true], results.Select(one => one.Moved));
        Assert.True(File.Exists(Path.Combine(into, "Silo.S03E03.mkv")));
    }

    /// <remarks>
    /// <para>
    /// The download folder and the intake folder are very often on different
    /// disks — the download lands on the fast one and the library lives on the
    /// big one — and a move across volumes is a copy whatever the API is
    /// called. This does it for real, between the drive this repository is on
    /// and the drive the temporary folder is on.
    /// </para>
    /// <para>
    /// If this machine only has one, the test says so rather than passing
    /// quietly: a cross-volume staging that has never been run is one nobody
    /// knows about until the owner's server has two disks.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task StagingAcrossVolumesWorks()
    {
        string across = Path.Combine(AnotherVolume(), "nomercy-staging-" + Guid.NewGuid().ToString("n")[..8]);

        Directory.CreateDirectory(across);

        try
        {
            byte[] content = Content(2 * 1024 * 1024);

            await File.WriteAllBytesAsync(Path.Combine(across, "Silo.S03E06.mkv"), content);

            string into = Path.Combine(_root, "intake");

            StagedResult result = Assert.Single(await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
                [new("Silo.S03E06.mkv", Episode(6), content.Length)],
                across,
                into,

                // No release name, so the file keeps its own.
                show: null,
                resolution: null,
                CancellationToken.None));

            Assert.True(result.Moved);
            Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(into, "Silo.S03E06.mkv")));
            Assert.False(File.Exists(Path.Combine(across, "Silo.S03E06.mkv")));
        }
        finally
        {
            Directory.Delete(across, recursive: true);
        }
    }

    /// <summary>
    /// Somewhere on a different device from the intake folder.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A volume is not a drive letter everywhere. On Windows it is: the drive
    /// this repository is on and the drive the temporary folder is on are two
    /// devices, and comparing path roots finds them. On Linux every absolute
    /// path roots at <c>/</c> however many devices are mounted, so that
    /// comparison finds nothing and the test failed on the first CI build there
    /// had ever been — for want of a second drive letter on a machine with
    /// several file systems.
    /// </para>
    /// <para>
    /// <c>/dev/shm</c> is the one that is always there and always its own:
    /// it is a tmpfs, so a move out of it is a copy exactly as a move between
    /// two disks is.
    /// </para>
    /// <para>
    /// If no second device can be found this says so and fails, rather than
    /// passing quietly. A cross-volume staging that has never run is one nobody
    /// knows about until the owner's server has two disks.
    /// </para>
    /// </remarks>
    private static string AnotherVolume()
    {
        if (OperatingSystem.IsWindows())
        {
            string here = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))!;
            string temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

            Assert.NotEqual(here, temp);

            return here;
        }

        foreach (string candidate in new[] { "/dev/shm", "/run/shm" })
        {
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException(
            "No second device to stage across: neither /dev/shm nor /run/shm is mounted, and every "
            + "path here roots at /. Cross-volume staging is what turns a move into a copy, and it "
            + "is not being tested on this machine.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    /// <remarks>
    /// <para>
    /// <strong>The torrent client is still holding the file.</strong> It keeps
    /// every file of a running torrent open for reading and writing and shares
    /// it both ways, because it seeds out of the same handle it downloaded
    /// into. A copy that asks to share the file for reading alone is refused by
    /// Windows before a byte is read: the share mode has to allow what the
    /// existing handle already has.
    /// </para>
    /// <para>
    /// That is why nothing had ever reached the owner's library. On
    /// 23 August 2026 four finished episodes sat in the download folder with
    /// their grabs marked failed, the intake folder held nothing but empty
    /// folders from the previous plugin, and no grab had ever been marked done.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFileTheClientIsStillHoldingIsStagedAnyway()
    {
        byte[] content = Content(2 * 1024 * 1024);
        string from = Folder("incomplete-held");
        string into = Path.Combine(_root, "intake-held");
        string path = Path.Combine(from, "Silo.S03E07.mkv");

        await File.WriteAllBytesAsync(path, content);

        // Exactly how TorrentDisk holds it while the torrent is running.
        await using FileStream held = new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        IReadOnlyList<StagedResult> results = await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [new("Silo.S03E07.mkv", Episode(7), content.Length)],
            from,
            into,
            // No release name, so the file keeps its own.
            show: null,
            resolution: null,
            CancellationToken.None);

        StagedResult one = Assert.Single(results);

        Assert.True(one.Moved, one.Reason);
        Assert.Equal(content, await File.ReadAllBytesAsync(Path.Combine(into, "Silo.S03E07.mkv")));
    }

    /// <remarks>
    /// <para>
    /// <strong>The staged file carries the release, not the uploader's
    /// spelling.</strong> Everything else already does: the grab records the
    /// release the plugin chose, the pages show it, staging matches by it. The
    /// file itself was the one place a site's own tag still leaked through —
    /// <c>silo.s03e04.1080p.web.h264-cakes[EZTVx.to].mkv</c>.
    /// </para>
    /// <para>
    /// It is the name the server parses to work out what the file is, so it
    /// ought to be the one this plugin decided on.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AStagedFileIsNamedAfterTheEpisodeItHolds()
    {
        byte[] content = Content(1024 * 1024);
        string from = Folder("incomplete-named");
        string into = Path.Combine(_root, "intake-named");

        await File.WriteAllBytesAsync(
            Path.Combine(from, "silo.s03e06.1080p.web.h264-cakes[EZTVx.to].mkv"),
            content);

        IReadOnlyList<StagedResult> results = await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [new("silo.s03e06.1080p.web.h264-cakes[EZTVx.to].mkv", Episode(6), content.Length)],
            from,
            into,
            Silo,
            "1080p",
            CancellationToken.None);

        StagedResult one = Assert.Single(results);

        Assert.True(one.Moved, one.Reason);
        Assert.Equal(Path.Combine(into, "Silo.2023.S03E06.1080p.mkv"), one.Path);
        Assert.Equal(content, await File.ReadAllBytesAsync(one.Path!));
    }

    /// <remarks>
    /// <para>
    /// <strong>Two releases of one episode are one file.</strong> The owner's
    /// intake folder held ten files for five episodes — Lucky S01E07, Sugar
    /// S02E02, E04, E05 and E08 — in pairs identical in size and differing
    /// only by the site's tag on the end of the name.
    /// </para>
    /// <para>
    /// They came from two rows of one torrent, one carrying the indexer's
    /// spelling of the release and one the plugin's, and the file was named
    /// after whichever row staged it. Named after the episode instead, both
    /// come to one path: there is nowhere for a second copy to be.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TwoReleasesOfOneEpisodeComeToOneFile()
    {
        byte[] content = Content(1024 * 1024);
        string from = Folder("incomplete-twice");
        string into = Path.Combine(_root, "intake-twice");

        foreach (string name in new[]
        {
            "Sugar.2024.S02E04.1080p.WEB.H264-CAKES.mkv",
            "Sugar 2024 S02E04 1080p WEB H264-CAKES EZTV.mkv",
        })
        {
            await File.WriteAllBytesAsync(Path.Combine(from, name), content);
        }

        Stager stager = new(new ActivityJournal(), new CapturingLogger());

        StagedResult first = Assert.Single(await stager.MoveAsync(
            [new("Sugar.2024.S02E04.1080p.WEB.H264-CAKES.mkv", Episode(4), content.Length)],
            from,
            into,
            Silo,
            "1080p",
            CancellationToken.None));

        StagedResult second = Assert.Single(await stager.MoveAsync(
            [new("Sugar 2024 S02E04 1080p WEB H264-CAKES EZTV.mkv", Episode(4), content.Length)],
            from,
            into,
            Silo,
            "1080p",
            CancellationToken.None));

        Assert.True(first.Moved, first.Reason);
        Assert.True(second.Moved, second.Reason);
        Assert.Equal(first.Path, second.Path);
        Assert.Single(Directory.EnumerateFiles(into));
    }

    /// <remarks>
    /// A pack is several episodes under one release name. Named after the
    /// release they would overwrite each other; named after the episode each
    /// holds, they cannot — which is why a pack needs no special case any more.
    /// </remarks>
    [Fact]
    public async Task EveryFileOfAPackIsNamedAfterItsOwnEpisode()
    {
        byte[] content = Content(512 * 1024);
        string from = Folder("incomplete-pack");
        string into = Path.Combine(_root, "intake-pack");

        foreach (string name in new[] { "Silo.S03E06.mkv", "Silo.S03E07.mkv" })
        {
            await File.WriteAllBytesAsync(Path.Combine(from, name), content);
        }

        IReadOnlyList<StagedResult> results = await new Stager(new ActivityJournal(), new CapturingLogger()).MoveAsync(
            [
                new("Silo.S03E06.mkv", Episode(6), content.Length),
                new("Silo.S03E07.mkv", Episode(7), content.Length),
            ],
            from,
            into,
            Silo,
            "1080p",
            CancellationToken.None);

        Assert.All(results, one => Assert.True(one.Moved, one.Reason));
        Assert.True(File.Exists(Path.Combine(into, "Silo.2023.S03E06.1080p.mkv")));
        Assert.True(File.Exists(Path.Combine(into, "Silo.2023.S03E07.1080p.mkv")));
    }

    private string Folder(string name)
    {
        string path = Path.Combine(_root, name);

        Directory.CreateDirectory(path);

        return path;
    }

    private static byte[] Content(int length)
    {
        byte[] content = new byte[length];

        for (int at = 0; at < length; at++)
        {
            content[at] = (byte)(at % 251);
        }

        return content;
    }

    /// <summary>The show the staged episodes are of, as a library has it.</summary>
    private static Show Silo { get; } =
        new(42, "Silo", 2023, "01KZGKX2G0966V80H26EKGG5JA", LibraryKind.Television, "Silo");

    private static EpisodeKey Episode(int number)
    {
        return new(42, 3, number);
    }
}
