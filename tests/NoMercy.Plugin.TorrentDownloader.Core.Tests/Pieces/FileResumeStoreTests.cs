// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class FileResumeStoreTests
{
    private static TorrentMetadata Season() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(4)
        .WithFile("season/a.bin", "aaaabbbbcccc")
        .WithFile("season/b.bin", "dddd")
        .Build());

    private static TorrentMetadata Other() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("other")
        .WithPieceLength(4)
        .WithFile("other", "zzzz")
        .Build());

    [Fact]
    public async Task LoadAsync_ReturnsNullWhenNothingWasEverSaved()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);

        (await store.LoadAsync(Season(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_ReturnsExactlyWhatWasSaved()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();

        Bitfield saved = new(metadata.PieceCount);
        saved[0] = true;
        saved[3] = true;

        await store.SaveAsync(metadata, saved, CancellationToken.None);
        Bitfield? loaded = await store.LoadAsync(metadata, CancellationToken.None);

        loaded.Should().NotBeNull();
        loaded!.Length.Should().Be(metadata.PieceCount);
        loaded.SetCount.Should().Be(2);
        loaded[0].Should().BeTrue();
        loaded[1].Should().BeFalse();
        loaded[3].Should().BeTrue();
    }

    [Fact]
    public async Task SaveAsync_ReplacesTheEarlierRecord()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();

        Bitfield first = new(metadata.PieceCount);
        first[0] = true;
        await store.SaveAsync(metadata, first, CancellationToken.None);

        Bitfield second = new(metadata.PieceCount);
        second[0] = true;
        second[1] = true;
        await store.SaveAsync(metadata, second, CancellationToken.None);

        Bitfield? loaded = await store.LoadAsync(metadata, CancellationToken.None);

        loaded!.SetCount.Should().Be(2);
    }

    [Fact]
    public async Task LoadAsync_KeepsRecordsForDifferentTorrentsApart()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata season = Season();
        TorrentMetadata other = Other();

        Bitfield seasonField = new(season.PieceCount);
        seasonField[2] = true;
        await store.SaveAsync(season, seasonField, CancellationToken.None);

        (await store.LoadAsync(other, CancellationToken.None)).Should().BeNull();
        (await store.LoadAsync(season, CancellationToken.None))!.SetCount.Should().Be(1);
    }

    [Fact]
    public async Task LoadAsync_RefusesARecordWhoseInfoHashDoesNotMatch()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();
        await store.SaveAsync(metadata, new Bitfield(metadata.PieceCount), CancellationToken.None);

        // Corrupt the stored info hash in place. A record whose identity does not match
        // is not this torrent's record, whatever its file name says.
        string path = Directory.GetFiles(folder.Path).Single();
        byte[] record = await File.ReadAllBytesAsync(path);
        record[6] ^= 0xFF;
        await File.WriteAllBytesAsync(path, record);

        (await store.LoadAsync(metadata, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_RefusesATruncatedRecord()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();
        await store.SaveAsync(metadata, new Bitfield(metadata.PieceCount), CancellationToken.None);

        string path = Directory.GetFiles(folder.Path).Single();
        byte[] record = await File.ReadAllBytesAsync(path);
        await File.WriteAllBytesAsync(path, record[..(record.Length - 1)]);

        (await store.LoadAsync(metadata, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task LoadAsync_RefusesAFileThatIsNotARecordAtAll()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();
        await store.SaveAsync(metadata, new Bitfield(metadata.PieceCount), CancellationToken.None);

        string path = Directory.GetFiles(folder.Path).Single();
        await File.WriteAllTextAsync(path, "this is not a resume record");

        (await store.LoadAsync(metadata, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_LeavesNoTemporaryFileBehind()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();

        await store.SaveAsync(metadata, new Bitfield(metadata.PieceCount), CancellationToken.None);

        Directory.GetFiles(folder.Path).Should().ContainSingle();
    }

    [Fact]
    public async Task SaveAsync_RejectsABitfieldOfTheWrongSize()
    {
        using TempFolder folder = new();
        FileResumeStore store = new(folder.Path);
        TorrentMetadata metadata = Season();

        Func<Task> save = () => store.SaveAsync(metadata, new Bitfield(metadata.PieceCount + 1), CancellationToken.None);

        await save.Should().ThrowAsync<ArgumentException>();
    }
}
