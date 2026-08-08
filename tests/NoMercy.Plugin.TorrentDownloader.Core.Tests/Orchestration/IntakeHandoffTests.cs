// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Orchestration;

public class IntakeHandoffTests
{
    private static readonly EpisodeKey Key = new(1, 1, 1);

    private const long BigEnough = 60 * 1024 * 1024;

    private static async Task WriteAsync(string path, long length)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        await using FileStream stream = File.Create(path);
        stream.SetLength(length);
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_MovesTheEpisodeWhereTheServerWillFindIt()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);

        bool moved = await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None);

        moved.Should().BeTrue();
        File.Exists(Path.Combine(intake.Path, "S01E01.mkv")).Should().BeTrue();
        File.Exists(Path.Combine(completed, "S01E01.mkv")).Should().BeFalse();
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_TakesEveryEpisodeInASeasonPack()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);
        await WriteAsync(Path.Combine(completed, "S01E02.mkv"), BigEnough);

        await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None);

        Directory.GetFiles(intake.Path).Should().HaveCount(2);
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_LeavesTheJunkThatCameWithIt()
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

        await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None);

        Directory.GetFiles(intake.Path).Should().ContainSingle();
        Path.GetFileName(Directory.GetFiles(intake.Path)[0]).Should().Be("S01E01.mkv");
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_FindsAnEpisodeNestedInASubfolder()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "Season 01", "S01E01.mkv"), BigEnough);

        (await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None))
            .Should().BeTrue();

        File.Exists(Path.Combine(intake.Path, "S01E01.mkv")).Should().BeTrue();
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_SaysNoWhenThereIsNoVideoAtAll()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "readme.txt"), 100);

        // Saying no leaves the grab unfinished, which is right: something arrived that
        // is not what was asked for, and pretending otherwise hides it.
        (await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_SaysNoWhenTheFolderIsNotThere()
    {
        using TempFolder intake = new();

        (await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync("/nowhere/at/all", Key, CancellationToken.None))
            .Should().BeFalse();
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_NeverOverwritesSomethingAlreadyWaiting()
    {
        using TempFolder downloads = new();
        using TempFolder intake = new();

        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);
        await File.WriteAllTextAsync(Path.Combine(intake.Path, "S01E01.mkv"), "somebody else's file");

        await new IntakeHandoff(intake.Path).MoveIntoIntakeAsync(completed, Key, CancellationToken.None);

        // Whatever is already there is either a half-finished earlier attempt or somebody
        // else's, and both are worse to clobber than to leave alone.
        (await File.ReadAllTextAsync(Path.Combine(intake.Path, "S01E01.mkv"))).Should().Be("somebody else's file");
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_CreatesTheIntakeFolderIfItIsNotThereYet()
    {
        using TempFolder downloads = new();
        using TempFolder parent = new();

        string intake = Path.Combine(parent.Path, "intake");
        string completed = downloads.File("season");
        await WriteAsync(Path.Combine(completed, "S01E01.mkv"), BigEnough);

        (await new IntakeHandoff(intake).MoveIntoIntakeAsync(completed, Key, CancellationToken.None))
            .Should().BeTrue();

        File.Exists(Path.Combine(intake, "S01E01.mkv")).Should().BeTrue();
    }
}
