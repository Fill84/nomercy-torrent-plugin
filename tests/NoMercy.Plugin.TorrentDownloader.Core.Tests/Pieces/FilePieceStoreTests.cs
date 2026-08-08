// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pieces;

public class FilePieceStoreTests
{
    /// <summary>
    /// Reads a file the store still holds open for writing. A reader must permit writing
    /// in its own share mode or Windows refuses the handle, which is exactly what anything
    /// inspecting a partially downloaded file has to do.
    /// </summary>
    private static async Task<string> ReadWhileOpenAsync(string path)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using StreamReader reader = new(stream);
        return await reader.ReadToEndAsync();
    }

    private static TorrentBuilder Season() => new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(8)
        .WithFile("season/a.bin", "aaaa")
        .WithFile("season/b.bin", "bbbbbb")
        .WithFile("season/c.bin", "cc");

    [Fact]
    public async Task WritePieceAsync_SplitsAPieceAcrossTheFilesItCovers()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(0, Encoding.UTF8.GetBytes("aaaabbbb"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        (await ReadWhileOpenAsync(folder.File("season", "a.bin"))).Should().Be("aaaa");
        (await ReadWhileOpenAsync(folder.File("season", "b.bin"))).Should().StartWith("bbbb");
    }

    [Fact]
    public async Task WritePieceAsync_WritesTheWholeTorrentBackByteForByte()
    {
        using TempFolder folder = new();
        TorrentBuilder builder = Season();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        byte[] content = builder.Content();

        using (FilePieceStore store = new(metadata, folder.Path))
        {
            for (int index = 0; index < metadata.PieceCount; index++)
            {
                int length = metadata.LengthOfPiece(index);
                await store.WritePieceAsync(
                    index,
                    content.AsMemory((int)(index * metadata.PieceLength), length),
                    CancellationToken.None);
            }

            await store.FlushAsync(CancellationToken.None);
        }

        (await File.ReadAllTextAsync(folder.File("season", "a.bin"))).Should().Be("aaaa");
        (await File.ReadAllTextAsync(folder.File("season", "b.bin"))).Should().Be("bbbbbb");
        (await File.ReadAllTextAsync(folder.File("season", "c.bin"))).Should().Be("cc");
    }

    [Fact]
    public async Task ReadPieceAsync_ReturnsWhatWasWritten()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(1, Encoding.UTF8.GetBytes("bbcc"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        byte[] read = await store.ReadPieceAsync(1, CancellationToken.None);

        Encoding.UTF8.GetString(read).Should().Be("bbcc");
    }

    [Fact]
    public async Task ReadPieceAsync_ReturnsZeroesForAPieceThatWasNeverWritten()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        byte[] read = await store.ReadPieceAsync(0, CancellationToken.None);

        read.Should().HaveCount(8).And.OnlyContain(value => value == 0);
        PieceVerifier.Matches(metadata, 0, read).Should().BeFalse();
    }

    [Fact]
    public async Task WritePieceAsync_CreatesTheFoldersTheTorrentNames()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(new TorrentBuilder()
            .WithName("deep")
            .WithPieceLength(4)
            .WithFile("deep/sub/one.bin", "abcd")
            .WithFile("deep/sub/two.bin", "efgh")
            .Build());
        using FilePieceStore store = new(metadata, folder.Path);

        await store.WritePieceAsync(0, Encoding.UTF8.GetBytes("abcd"), CancellationToken.None);
        await store.FlushAsync(CancellationToken.None);

        File.Exists(folder.File("deep", "sub", "one.bin")).Should().BeTrue();
    }

    [Fact]
    public async Task WritePieceAsync_RejectsAPieceOfTheWrongLength()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        using FilePieceStore store = new(metadata, folder.Path);

        Func<Task> write = () => store.WritePieceAsync(0, Encoding.UTF8.GetBytes("short"), CancellationToken.None);

        await write.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task WritePieceAsync_RefusesToRunAfterDisposal()
    {
        using TempFolder folder = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(Season().Build());
        FilePieceStore store = new(metadata, folder.Path);
        store.Dispose();

        Func<Task> write = () => store.WritePieceAsync(0, Encoding.UTF8.GetBytes("aaaabbbb"), CancellationToken.None);

        await write.Should().ThrowAsync<ObjectDisposedException>();
    }
}
