// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

/// <summary>
/// The whole engine, end to end, against a seeder in the same process. No network,
/// no external client, no swarm that might not be there today.
/// </summary>
public class DownloadEndToEndTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(60));

    /// <summary>Three files over eight pieces of 4 KiB, so pieces cross file boundaries.</summary>
    private static TorrentBuilder SeasonPack()
    {
        byte[] first = Filler(10_000, seed: 1);
        byte[] second = Filler(14_000, seed: 2);
        byte[] third = Filler(8_768, seed: 3);

        return new TorrentBuilder()
            .WithName("season")
            .WithPieceLength(4096)
            .WithFile("season/e01.mkv", first)
            .WithFile("season/e02.mkv", second)
            .WithFile("season/season.nfo", third);
    }

    private static byte[] Filler(int length, int seed)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
            bytes[index] = (byte)((index * 31 + seed * 7) % 251);

        return bytes;
    }

    private static async Task ConnectAsync(
        TorrentSession session,
        TestSeeder seeder,
        TorrentMetadata metadata,
        List<Task> serving,
        CancellationToken ct)
    {
        (Stream ours, Stream theirs) = DuplexPair.Create();

        Task<PeerConnection> dialling = PeerConnection.DialAsync(ours, metadata, Handshake.NewPeerId(), ct);
        Task seeding = seeder.ServeAsync(theirs, ct);

        session.AddPeer(await dialling, ct);
        serving.Add(seeding);
    }

    [Fact]
    public async Task Download_CompletesAMultiFileTorrentByteForByte()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        TorrentBuilder builder = SeasonPack();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        byte[] content = builder.Content();

        using FilePieceStore store = new(metadata, folder.Path);
        FileResumeStore resume = new(resumeFolder.Path);

        await using TorrentSession session = new(metadata, store, resume, new Bitfield(metadata.PieceCount), SwarmPolicy.Default);
        Task brain = session.RunAsync(deadline.Token);

        List<Task> serving = [];
        await ConnectAsync(session, new TestSeeder(metadata, content), metadata, serving, deadline.Token);

        await session.Completion.WaitAsync(deadline.Token);

        session.IsComplete.Should().BeTrue();
        await store.FlushAsync(deadline.Token);

        // Every file, byte for byte, split across pieces that cross their boundaries.
        (await ReadAsync(folder, "season", "e01.mkv")).Should().Equal(Filler(10_000, 1));
        (await ReadAsync(folder, "season", "e02.mkv")).Should().Equal(Filler(14_000, 2));
        (await ReadAsync(folder, "season", "season.nfo")).Should().Equal(Filler(8_768, 3));
    }

    [Fact]
    public async Task Download_ResumesAfterARestartInsteadOfStartingOver()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        TorrentBuilder builder = SeasonPack();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        byte[] content = builder.Content();
        FileResumeStore resume = new(resumeFolder.Path);

        // A seeder that hangs up partway, standing in for a reboot mid-download.
        using (FilePieceStore firstStore = new(metadata, folder.Path))
        {
            await using TorrentSession first = new(metadata, firstStore, resume, new Bitfield(metadata.PieceCount), SwarmPolicy.Default);
            Task brain = first.RunAsync(deadline.Token);

            List<Task> serving = [];
            TestSeeder quitter = new(metadata, content) { HangUpAfterBlocks = 3 };
            await ConnectAsync(first, quitter, metadata, serving, deadline.Token);

            await WaitUntilAsync(() => first.Have.SetCount >= 3, deadline.Token);
            first.Have.IsComplete.Should().BeFalse();
        }

        Bitfield? recovered = await resume.LoadAsync(metadata, deadline.Token);
        recovered.Should().NotBeNull();
        recovered!.SetCount.Should().BeGreaterThan(0);
        int alreadyHeld = recovered.SetCount;

        // Restart with what survived and finish against an honest seeder.
        using FilePieceStore secondStore = new(metadata, folder.Path);
        await using TorrentSession second = new(metadata, secondStore, resume, recovered, SwarmPolicy.Default);
        Task secondBrain = second.RunAsync(deadline.Token);

        List<Task> more = [];
        TestSeeder honest = new(metadata, content);
        await ConnectAsync(second, honest, metadata, more, deadline.Token);

        await second.Completion.WaitAsync(deadline.Token);
        await secondStore.FlushAsync(deadline.Token);

        second.IsComplete.Should().BeTrue();

        // The point of resume: the pieces already held were not fetched a second time.
        honest.BlocksServed.Should().Be(metadata.PieceCount - alreadyHeld);
        (await ReadAsync(folder, "season", "e02.mkv")).Should().Equal(Filler(14_000, 2));
    }

    [Fact]
    public async Task Download_FinishesEvenWhenOnePeerKeepsSendingRubbish()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        TorrentBuilder builder = SeasonPack();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        byte[] content = builder.Content();

        using FilePieceStore store = new(metadata, folder.Path);
        FileResumeStore resume = new(resumeFolder.Path);

        await using TorrentSession session = new(metadata, store, resume, new Bitfield(metadata.PieceCount), SwarmPolicy.Default);
        Task brain = session.RunAsync(deadline.Token);

        List<Task> serving = [];

        TestSeeder liar = new(metadata, content);
        liar.CorruptPieces.UnionWith(Enumerable.Range(0, metadata.PieceCount));

        await ConnectAsync(session, liar, metadata, serving, deadline.Token);
        await ConnectAsync(session, new TestSeeder(metadata, content), metadata, serving, deadline.Token);

        await session.Completion.WaitAsync(deadline.Token);
        await store.FlushAsync(deadline.Token);

        // The liar is banned once its failures form a pattern, and the honest peer
        // carries the download to the end. Nothing corrupt reaches the disk.
        session.IsComplete.Should().BeTrue();
        (await ReadAsync(folder, "season", "e01.mkv")).Should().Equal(Filler(10_000, 1));
    }

    private static async Task<byte[]> ReadAsync(TempFolder folder, params string[] parts)
    {
        await using FileStream stream = new(folder.File(parts), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        byte[] contents = new byte[stream.Length];
        await stream.ReadExactlyAsync(contents);
        return contents;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(20, ct);
        }
    }
}
