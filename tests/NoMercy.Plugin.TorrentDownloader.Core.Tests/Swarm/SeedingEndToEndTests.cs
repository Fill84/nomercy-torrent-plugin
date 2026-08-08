// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Pieces;
using NoMercy.Plugin.TorrentDownloader.Core.Swarm;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Swarm;

/// <summary>
/// Proving the gate from the outside: a stranger asks us for a piece and finds out
/// what this plugin will and will not do.
/// </summary>
public class SeedingEndToEndTests
{
    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(30));

    private static TorrentMetadata Metadata() => MetadataParser.FromTorrentFile(new TorrentBuilder()
        .WithName("four")
        .WithPieceLength(4)
        .WithFile("four", "aaaabbbbccccdddd")
        .Build());

    private static Bitfield Everything(int count)
    {
        Bitfield field = new(count);

        for (int index = 0; index < count; index++)
            field[index] = true;

        return field;
    }

    private static async Task<(TorrentSession Session, PeerConnection Stranger, Task Brain)> ServeAsync(
        TempFolder folder,
        TempFolder resumeFolder,
        TorrentOrigin origin,
        CancellationToken ct)
    {
        TorrentMetadata metadata = Metadata();
        FilePieceStore store = new(metadata, folder.Path);

        for (int index = 0; index < metadata.PieceCount; index++)
        {
            string piece = new((char)('a' + index), 4);
            await store.WritePieceAsync(index, Encoding.ASCII.GetBytes(piece), ct);
        }

        await store.FlushAsync(ct);

        Bitfield have = Everything(metadata.PieceCount);

        PieceServer server = new(metadata, store, SwarmPolicy.Default, origin, have) { DownloadedBytes = 16 };
        TorrentSession session = new(metadata, store, new FileResumeStore(resumeFolder.Path), have, SwarmPolicy.Default, server);

        Task brain = session.RunAsync(ct);

        (Stream ours, Stream theirs) = DuplexPair.Create();
        Task<PeerConnection> accepting = PeerConnection.AcceptAsync(ours, metadata, Handshake.NewPeerId(), ct);
        Task<PeerConnection> dialling = PeerConnection.DialAsync(theirs, metadata, Handshake.NewPeerId(), ct);

        session.AddPeer(await accepting, ct);

        return (session, await dialling, brain);
    }

    [Fact]
    public async Task APrivateTorrentConfiguredToSeed_AnswersAStrangersRequest()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        (TorrentSession session, PeerConnection stranger, _) =
            await ServeAsync(folder, resumeFolder, TorrentOrigin.PrivateSeeding, deadline.Token);

        await using (session)
        await using (stranger)
        {
            await stranger.SendAsync(new Interested(), deadline.Token);
            (await stranger.ReceiveAsync(deadline.Token)).Should().Be(new Unchoke());

            await stranger.SendAsync(new Request(1, 0, 4), deadline.Token);
            PeerMessage answer = await stranger.ReceiveAsync(deadline.Token);

            answer.Should().Be(new PieceBlock(1, 0, "bbbb"u8.ToArray()));
        }
    }

    [Fact]
    public async Task APublicTorrent_NeverUnchokesAndNeverAnswers()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        (TorrentSession session, PeerConnection stranger, _) =
            await ServeAsync(folder, resumeFolder, TorrentOrigin.Public, deadline.Token);

        await using (session)
        await using (stranger)
        {
            await stranger.SendAsync(new Interested(), deadline.Token);
            await stranger.SendAsync(new Request(1, 0, 4), deadline.Token);

            // Nothing comes back. Not a rejection, not a choke - silence, because a
            // public torrent has no path to uploading at all.
            using CancellationTokenSource shortWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            shortWait.CancelAfter(TimeSpan.FromSeconds(2));

            Func<Task> waiting = async () => await stranger.ReceiveAsync(shortWait.Token);

            await waiting.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    [Fact]
    public async Task APrivateTorrentWithSeedingOff_AlsoAnswersNothing()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder folder = new();
        using TempFolder resumeFolder = new();

        (TorrentSession session, PeerConnection stranger, _) =
            await ServeAsync(folder, resumeFolder, TorrentOrigin.PrivateWithoutSeeding, deadline.Token);

        await using (session)
        await using (stranger)
        {
            await stranger.SendAsync(new Interested(), deadline.Token);
            await stranger.SendAsync(new Request(0, 0, 4), deadline.Token);

            using CancellationTokenSource shortWait = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            shortWait.CancelAfter(TimeSpan.FromSeconds(2));

            Func<Task> waiting = async () => await stranger.ReceiveAsync(shortWait.Token);

            await waiting.Should().ThrowAsync<OperationCanceledException>();
        }
    }
}
