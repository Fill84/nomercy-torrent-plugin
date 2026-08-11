// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;

public class FinishedFolderMoverTests
{
    private const long BigEnough = 60 * 1024 * 1024;

    private static async Task WriteAsync(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using FileStream stream = File.Create(path);
        stream.SetLength(length);
    }

    [Fact]
    public async Task MoveAsync_MovesTheEpisodeWhereTheServerWillFindIt()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);

        string? moved = await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None);

        moved.Should().Be(Path.Combine(intake.Path, "season"));
        File.Exists(Path.Combine(intake.Path, "season", "S01E01.mkv")).Should().BeTrue();
        File.Exists(Path.Combine(completed, "S01E01.mkv")).Should().BeFalse();
    }

    /// <summary>
    /// The single-file torrent, which is what most episode releases are - and the case this
    /// method silently did nothing for.
    ///
    /// <para>
    /// A torrent's "name" is a directory for a multi-file torrent and a filename for a
    /// single-file one, and the engine reports whichever it is as the completed path. This
    /// began by testing Directory.Exists and giving up, so on a real server three finished
    /// episodes sat at 100% in the download folder and were retried every minute forever:
    /// the move never happened, no encode was ever queued, and nothing reached the library.
    /// </para>
    /// </summary>
    [Fact]
    public async Task MoveAsync_TakesASingleFileTorrentWhereTheCompletedPathIsTheFile()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = Path.Combine(downloads.Path, "Sugar.2024.S02E05.1080p.WEB.h264-ETHEL.mkv");
        await WriteAsync(completed, BigEnough);

        string? moved = await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None);

        string expected = Path.Combine(intake.Path, "Sugar.2024.S02E05.1080p.WEB.h264-ETHEL");

        moved.Should().Be(expected, "the server reads the release name off the folder");
        File.Exists(Path.Combine(expected, "Sugar.2024.S02E05.1080p.WEB.h264-ETHEL.mkv")).Should().BeTrue();
        File.Exists(completed).Should().BeFalse("it moved rather than copied");
    }

    // The same guard the folder path has: something that is not video is not the episode,
    // whatever the torrent called itself.
    [Fact]
    public async Task MoveAsync_RefusesASingleFileThatIsNotVideo()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = Path.Combine(downloads.Path, "Lucky 2026 S01E06 1080p WEB h264-ETHEL.scr");
        await WriteAsync(completed, BigEnough);

        (await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None))
            .Should().BeNull();

        File.Exists(completed).Should().BeTrue("it was left where it was rather than moved into the library's path");
    }

    [Fact]
    public async Task MoveAsync_TakesEveryEpisodeInASeasonPack()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);
        await WriteAsync(Path.Combine(completed, "S01E02.mkv"), BigEnough);

        await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None);

        Directory.GetFiles(Path.Combine(intake.Path, "season")).Should().HaveCount(2);
    }

    [Fact]
    public async Task MoveAsync_LeavesTheJunkThatCameWithIt()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);
        await WriteAsync(Path.Combine(completed, "release.nfo"), 2000);
        await WriteAsync(Path.Combine(completed, "cover.jpg"), 40000);

        // A sample is a video file and is not the episode. Moving it makes the server
        // import a two-minute clip as the show.
        await WriteAsync(Path.Combine(completed, "sample", "sample.mkv"), 5 * 1024 * 1024);

        await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None);

        string[] moved = Directory.GetFiles(Path.Combine(intake.Path, "season"));
        moved.Should().ContainSingle();
        Path.GetFileName(moved[0]).Should().Be("S01E01.mkv");
    }

    [Fact]
    public async Task MoveAsync_FindsAnEpisodeNestedInASubfolder()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "Season 01", "S01E01.mkv"), BigEnough);

        (await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None))
            .Should().NotBeNull();

        File.Exists(Path.Combine(intake.Path, "season", "S01E01.mkv")).Should().BeTrue();
    }

    [Fact]
    public async Task MoveAsync_SaysNoWhenThereIsNoVideoAtAll()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "readme.txt"), 100);

        // Saying no leaves the grab unfinished, which is right: something arrived that
        // is not what was asked for, and pretending otherwise hides it.
        (await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task MoveAsync_SaysNoWhenTheFolderIsNotThere()
    {
        using TempFolder intake = new();

        (await new FinishedFolderMover(intake.Path).MoveAsync("/nowhere/at/all", CancellationToken.None))
            .Should().BeNull();
    }

    [Fact]
    public async Task MoveAsync_NeverOverwritesSomethingAlreadyWaiting()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);
        await File.WriteAllTextAsync(Path.Combine(intake.Path, "S01E01.mkv"), "somebody else's file");

        await new FinishedFolderMover(intake.Path).MoveAsync(completed, CancellationToken.None);

        // Whatever is already there is either a half-finished earlier attempt or somebody
        // else's, and both are worse to clobber than to leave alone.
        (await File.ReadAllTextAsync(Path.Combine(intake.Path, "S01E01.mkv"))).Should().Be("somebody else's file");
    }

    [Fact]
    public async Task MoveAsync_CreatesTheIntakeFolderIfItIsNotThereYet()
    {
        using TempFolder downloads = new();
        using TempFolder parent = new();

        string intake = Path.Combine(parent.Path, "intake");
        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);

        (await new FinishedFolderMover(intake).MoveAsync(completed, CancellationToken.None))
            .Should().NotBeNull();

        File.Exists(Path.Combine(intake, "season", "S01E01.mkv")).Should().BeTrue();
    }
}
