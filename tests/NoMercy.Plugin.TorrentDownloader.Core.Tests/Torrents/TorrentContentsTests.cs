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

    /// <summary>
    /// A release beside a program still downloads - and the program is never written. The
    /// torrent is not refused, because refusing on the strength of one file would refuse
    /// almost every real release for its nfo; the selection below is what keeps the disk
    /// clean.
    /// </summary>
    [Theory]
    [InlineData("setup.exe")]
    [InlineData("install.msi")]
    [InlineData("payload.js")]
    [InlineData("release.nfo")]
    [InlineData("release.rar")]
    [InlineData("poster.jpg")]
    [InlineData("Show.S01E01.srt")]
    [InlineData("weird.qqq")]
    [InlineData("README")]
    public void IsVideo_IsFalseForEverythingThatIsNotVideo(string name)
    {
        TorrentContents.IsVideo(new FileEntry([name], 100, 0)).Should().BeFalse();
    }

    [Theory]
    [InlineData("Show.S01E01.1080p.WEB.h264-GROUP.mkv")]
    [InlineData("Show.S01E01.mp4")]
    [InlineData("Show.S01E01.AVI")]
    [InlineData("Show.S01E01.m2ts")]
    public void IsVideo_IsTrueForVideo(string name)
    {
        TorrentContents.IsVideo(new FileEntry([name], 100, 0)).Should().BeTrue();
    }

    [Fact]
    public void Refuse_LetsAReleaseWithJunkBesideItThrough()
    {
        TorrentContents.Refuse(With("Show.S01E01.1080p.mkv", "setup.exe", "release.nfo"))
            .Should().BeNull("the torrent is fine; only its video is written");
    }

    /// <summary>
    /// Case is whatever the person who built the torrent felt like, and ".SCR" is the
    /// obvious way past a naive check.
    /// </summary>
    [Theory]
    [InlineData("Episode.SCR")]
    [InlineData("Episode.Exe")]
    public void Refuse_IsNotFooledByCasing(string name)
    {
        TorrentContents.Refuse(With(name)).Should().NotBeNull();
    }

    [Fact]
    public void IsVideo_ReadsTheExtensionOffTheFilesOwnName()
    {
        TorrentContents.IsVideo(new FileEntry(["Subs", "extras", "codec.exe"], 100, 0)).Should().BeFalse();
        TorrentContents.IsVideo(new FileEntry(["Season 1", "Show.S01E01.mkv"], 100, 0)).Should().BeTrue();
    }

    // A torrent of nothing but companions is not what was searched for.
    [Fact]
    public void Refuse_ATorrentWithNoVideoInIt()
    {
        TorrentContents.Refuse(With("cover.jpg", "readme.nfo")).Should().Contain("no video");
    }

    /// <summary>
    /// The property that matters, and the reason this is an allowlist rather than a list of
    /// dangerous extensions: something nobody thought of is refused by default. None of
    /// these is on any blocklist in this file, and every one of them is refused.
    /// </summary>
    /// <summary>
    /// The property that matters, and the reason this is an allowlist rather than a list of
    /// dangerous extensions: something nobody thought of is kept off the disk by default.
    /// None of these is on any blocklist in that file.
    /// </summary>
    [Theory]
    [InlineData("release.rar")]
    [InlineData("disc.iso")]
    [InlineData("thing.dmg")]
    [InlineData("payload.scpt")]
    [InlineData("macro.xlsm")]
    [InlineData("weird.qqq")]
    public void IsVideo_RefusesAnythingNobodyPutOnAList(string name)
    {
        TorrentContents.IsVideo(new FileEntry([name], 100, 0)).Should().BeFalse();
    }

    // A torrent of nothing but those is still refused outright: there is no episode in it.
    [Fact]
    public void Refuse_ATorrentOfNothingButArchives()
    {
        TorrentContents.Refuse(With("release.rar", "release.r00")).Should().Contain("no video");
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
        TorrentContents.Refuse(With(video, "release.nfo", "sample.txt", "poster.jpg", "release.sfv"))
            .Should().BeNull("refusing an nfo would refuse almost every real release");
    }

    [Fact]
    public void Refuse_LetsASeasonPackThrough()
    {
        TorrentContents.Refuse(With(
            "Show.S01E01.mkv",
            "Show.S01E02.mkv",
            "Show.S01E01.srt",
            "Show.S01E02.srt")).Should().BeNull();
    }
}
