// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Torrents;

/// <summary>
/// What this plugin will and will not put on somebody's disk.
///
/// <para>
/// Written the day a release named "Lucky 2026 S01E06 1080p WEB h264-ETHEL" arrived on a
/// real server as one 1.2 GB file called <c>.scr</c> - a Windows executable padded out to
/// look like an episode - and the engine wrote it out and marked it executable. The import
/// refused it afterwards, so it never reached the library, which is the wrong place to find
/// out: by then it is already on the machine.
/// </para>
/// </summary>
public class TorrentContentsTests
{
    private static TorrentMetadata With(params string[] names) =>
        new(
            InfoHash: new byte[20],
            Name: "release",
            PieceLength: 16384,
            PieceHashes: [new byte[20]],
            Files: [.. names.Select((name, index) => new FileEntry([name], 1000, index * 1000L))],
            Trackers: []);

    /// <summary>The exact file that reached a real server's disk.</summary>
    [Fact]
    public void Refuse_TheExecutableThatCalledItselfAnEpisode()
    {
        string? refusal = TorrentContents.Refuse(With("Lucky 2026 S01E06 1080p WEB h264-ETHEL.scr"));

        refusal.Should().NotBeNull();
        refusal.Should().Contain("program");
        refusal.Should().Contain(".scr");
    }

    [Theory]
    [InlineData("setup.exe")]
    [InlineData("install.msi")]
    [InlineData("run.bat")]
    [InlineData("payload.js")]
    [InlineData("thing.vbs")]
    [InlineData("script.ps1")]
    [InlineData("shortcut.lnk")]
    [InlineData("app.jar")]
    public void Refuse_AnythingThatRuns(string name)
    {
        TorrentContents.Refuse(With("Show.S01E01.1080p.mkv", name)).Should().NotBeNull(
            "a video in the same torrent does not make the program safe");
    }

    /// <summary>
    /// Case is whatever the person who built the torrent felt like. ".SCR" is the same
    /// threat as ".scr" and the obvious way past a naive check.
    /// </summary>
    [Theory]
    [InlineData("Episode.SCR")]
    [InlineData("Episode.Exe")]
    public void Refuse_IsNotFooledByCasing(string name)
    {
        TorrentContents.Refuse(With(name)).Should().NotBeNull();
    }

    [Fact]
    public void Refuse_AnExecutableBuriedInASubfolder()
    {
        TorrentMetadata metadata = new(
            InfoHash: new byte[20],
            Name: "release",
            PieceLength: 16384,
            PieceHashes: [new byte[20]],
            Files:
            [
                new FileEntry(["Show.S01E01.mkv"], 1000, 0),
                new FileEntry(["Subs", "extras", "codec.exe"], 1000, 1000),
            ],
            Trackers: []);

        TorrentContents.Refuse(metadata).Should().Contain("codec.exe");
    }

    // A torrent of images or archives is not what was searched for. Not dangerous, and
    // still not something to spend the owner's disk on.
    [Fact]
    public void Refuse_ATorrentWithNoVideoInIt()
    {
        TorrentContents.Refuse(With("cover.jpg", "readme.nfo")).Should().Contain("no video");
    }

    [Fact]
    public void Refuse_ATorrentThatListsNothing()
    {
        TorrentMetadata empty = new(
            InfoHash: new byte[20],
            Name: "release",
            PieceLength: 16384,
            PieceHashes: [new byte[20]],
            Files: [],
            Trackers: []);

        TorrentContents.Refuse(empty).Should().NotBeNull();
    }

    /// <summary>
    /// The ordinary case has to keep working. A scene release ships an nfo and often a
    /// sample beside the episode, and refusing those would refuse everything.
    /// </summary>
    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.h264-GROUP.mkv")]
    [InlineData("Show.S01E01.mp4")]
    [InlineData("Show.S01E01.avi")]
    public void Refuse_LetsAnOrdinaryReleaseThrough(string video)
    {
        TorrentContents.Refuse(With(video, "release.nfo", "sample.txt", "poster.jpg"))
            .Should().BeNull();
    }

    [Fact]
    public void Refuse_LetsSubtitlesAndSeveralEpisodesThrough()
    {
        TorrentContents.Refuse(With(
            "Show.S01E01.mkv",
            "Show.S01E02.mkv",
            "Show.S01E01.srt",
            "Show.S01E02.srt")).Should().BeNull();
    }
}
