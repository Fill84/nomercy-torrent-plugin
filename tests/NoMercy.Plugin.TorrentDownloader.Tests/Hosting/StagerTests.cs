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
        string here = Path.GetPathRoot(Path.GetFullPath(AppContext.BaseDirectory))!;
        string temp = Path.GetPathRoot(Path.GetFullPath(Path.GetTempPath()))!;

        Assert.NotEqual(here, temp);

        string across = Path.Combine(here, "nomercy-staging-" + Guid.NewGuid().ToString("n")[..8]);

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        GC.SuppressFinalize(this);
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

    private static EpisodeKey Episode(int number)
    {
        return new(42, 3, number);
    }
}
