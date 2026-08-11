// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Engine;

public class TorrentEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 8, 12, 0, 0, TimeSpan.Zero);

    private DateTimeOffset _now = Start;

    private static TorrentBuilder SeasonPack() => new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(4096)
        .WithFile("season/e01.mkv", Filler(9000))
        .WithFile("season/e02.mkv", Filler(7000));

    private static byte[] Filler(int length)
    {
        byte[] bytes = new byte[length];

        for (int index = 0; index < length; index++)
            bytes[index] = (byte)(index % 251);

        return bytes;
    }

    private static CancellationTokenSource Deadline() => new(TimeSpan.FromSeconds(60));

    private TorrentEngine Engine(
        TempFolder downloads,
        TempFolder state,
        FakeTracker tracker,
        IPeerDialer dialer,
        byte[] torrentFile,
        TimeSpan? noPeersTimeout = null,
        TimeSpan? metadataTimeout = null) =>
        new(
            [tracker],
            dialer,
            new FakeFetcher(torrentFile),
            new TorrentEngineOptions
            {
                DownloadFolder = downloads.Path,
                StateFolder = state.Path,
                NoPeersTimeout = noPeersTimeout ?? TimeSpan.FromMinutes(30),
                MetadataTimeout = metadataTimeout ?? TimeSpan.FromMinutes(5),
            },
            () => _now);

    [Fact]
    public async Task AddAsync_ReturnsTheInfoHashTheTorrentActuallyHas()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        TorrentBuilder builder = SeasonPack();
        await using TorrentEngine engine = Engine(downloads, state, new FakeTracker(), new RefusingDialer(), builder.Build());

        string hash = await engine.AddAsync(Request(), deadline.Token);

        hash.Should().Be(Convert.ToHexStringLower(builder.ExpectedInfoHash()));
    }

    [Fact]
    public async Task AddAsync_IsIdempotentForTheSameTorrent()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        await using TorrentEngine engine = Engine(downloads, state, new FakeTracker(), new RefusingDialer(), SeasonPack().Build());

        string first = await engine.AddAsync(Request(), deadline.Token);
        string second = await engine.AddAsync(Request(), deadline.Token);

        // Two episodes can want one season pack, and a retry can land while the first
        // attempt is still running. Neither is an error.
        first.Should().Be(second);
        (await engine.TransfersAsync(deadline.Token)).Should().ContainSingle();
    }

    /// <summary>
    /// A magnet nobody will describe is reported, not thrown.
    ///
    /// <para>
    /// This test used to assert the opposite - that AddAsync threw MetadataException and
    /// listed nothing - and the behaviour it was pinning is what kept a real server silent
    /// for a fortnight. The throw came out of the caller's search cycle before the grab was
    /// recorded, so the episodes behind it went unsearched and no page anywhere could say
    /// that a release had been chosen and lost.
    /// </para>
    ///
    /// <para>
    /// Giving up is still right. Doing it where somebody can see it is the change.
    /// </para>
    /// </summary>
    [Fact]
    public async Task AddAsync_ReportsAMagnetNobodyWillDescribeRatherThanThrowingIt()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        await using TorrentEngine engine = Engine(
            downloads, state, new FakeTracker(), new RefusingDialer(), SeasonPack().Build(),
            metadataTimeout: TimeSpan.FromSeconds(2));

        const string hash = "123456789abcdef00020417e2d5f2e7aff010203";

        string infoHash = await engine.AddAsync(
            Request() with { Source = $"magnet:?xt=urn:btih:{hash}" },
            deadline.Token);

        infoHash.Should().Be(hash);

        EngineTransfer failed = await Eventually(
            engine,
            transfer => transfer.State == EngineState.Failed,
            deadline.Token);

        failed.FailureReason.Should().Contain("no peer");
    }

    /// <summary>
    /// Polls the transfer list until it says what the test is waiting for.
    ///
    /// <para>
    /// The engine is deliberately polled rather than event-driven, and resolution now
    /// happens on a background task, so "wait for the state to change" is the honest shape
    /// of the assertion. The deadline is the test's own; there is no sleep-and-hope.
    /// </para>
    /// </summary>
    private static async Task<EngineTransfer> Eventually(
        TorrentEngine engine,
        Func<EngineTransfer, bool> until,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            EngineTransfer? found = (await engine.TransfersAsync(ct)).FirstOrDefault(until);

            if (found is not null)
                return found;

            await Task.Delay(50, ct);
        }

        throw new TimeoutException("the transfer list never reached the state the test was waiting for");
    }

    [Fact]
    public async Task TransfersAsync_ReportsATorrentThatHasNotStartedAsDownloading()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        TorrentBuilder builder = SeasonPack();
        await using TorrentEngine engine = Engine(downloads, state, new FakeTracker(), new RefusingDialer(), builder.Build());

        await engine.AddAsync(Request(), deadline.Token);

        IReadOnlyList<EngineTransfer> transfers = await engine.TransfersAsync(deadline.Token);

        transfers.Should().ContainSingle();
        transfers[0].State.Should().Be(EngineState.Downloading);
        transfers[0].BytesTotal.Should().Be(16000);
        transfers[0].BytesDone.Should().Be(0);
    }

    [Fact]
    public async Task TransfersAsync_CallsATorrentDeadOnceNobodyHasAnsweredForLongEnough()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        await using TorrentEngine engine = Engine(
            downloads, state, new FakeTracker(), new RefusingDialer(), SeasonPack().Build(),
            noPeersTimeout: TimeSpan.FromMinutes(30));

        await engine.AddAsync(Request(), deadline.Token);

        (await engine.TransfersAsync(deadline.Token))[0].State.Should().Be(EngineState.Downloading);

        _now = Start.AddMinutes(31);

        // Saying so lets the orchestrator try a different release rather than waiting
        // on a swarm that is not there.
        EngineTransfer dead = (await engine.TransfersAsync(deadline.Token))[0];
        dead.State.Should().Be(EngineState.Failed);
        dead.FailureReason.Should().Contain("no peers");
    }

    [Fact]
    public async Task TheEngine_DownloadsATorrentEndToEndFromTheTrackersPeers()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        TorrentBuilder builder = SeasonPack();
        byte[] torrentFile = builder.Build();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(torrentFile);

        FakeTracker tracker = new();
        tracker.Peers.Add(new PeerEndPoint(IPAddress.Parse("10.0.0.5"), 6881));

        SeedingDialer dialer = new(metadata, builder.Content());

        await using TorrentEngine engine = Engine(downloads, state, tracker, dialer, torrentFile);

        await engine.AddAsync(Request(), deadline.Token);

        // Everything the engine is for, in one line: a tracker named a peer, the engine
        // dialled it, and the files arrived.
        await WaitUntilAsync(
            async () => (await engine.TransfersAsync(deadline.Token))[0].State == EngineState.Completed,
            deadline.Token);

        EngineTransfer completed = (await engine.TransfersAsync(deadline.Token))[0];
        completed.BytesDone.Should().Be(16000);
        completed.CompletedFolder.Should().Be(Path.Combine(downloads.Path, "season"));

        await engine.DisposeAsync();

        (await File.ReadAllBytesAsync(Path.Combine(downloads.Path, "season", "e01.mkv"), deadline.Token))
            .Should().Equal(Filler(9000));
    }

    [Fact]
    public async Task RemoveAsync_TakesOnlyTheFilesThisTorrentWrote()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        string somebodyElse = Path.Combine(downloads.Path, "not-ours.mkv");
        await File.WriteAllTextAsync(somebodyElse, "another torrent's file", deadline.Token);

        TorrentBuilder builder = SeasonPack();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        SeedingDialer dialer = new(metadata, builder.Content());

        FakeTracker tracker = new();
        tracker.Peers.Add(new PeerEndPoint(IPAddress.Parse("10.0.0.5"), 6881));

        await using TorrentEngine engine = Engine(downloads, state, tracker, dialer, builder.Build());

        string hash = await engine.AddAsync(Request(), deadline.Token);

        await WaitUntilAsync(
            async () => (await engine.TransfersAsync(deadline.Token))[0].State == EngineState.Completed,
            deadline.Token);

        await engine.RemoveAsync(hash, deleteFiles: true, deadline.Token);

        // Deleting the whole download folder because one torrent failed is how a plugin
        // takes somebody's library with it.
        File.Exists(somebodyElse).Should().BeTrue();
        File.Exists(Path.Combine(downloads.Path, "season", "e01.mkv")).Should().BeFalse();
        (await engine.TransfersAsync(deadline.Token)).Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveAsync_LeavesTheFilesWhenNotAskedToDeleteThem()
    {
        using CancellationTokenSource deadline = Deadline();
        using TempFolder downloads = new();
        using TempFolder state = new();

        TorrentBuilder builder = SeasonPack();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());
        FakeTracker tracker = new();
        tracker.Peers.Add(new PeerEndPoint(IPAddress.Parse("10.0.0.5"), 6881));

        await using TorrentEngine engine = Engine(downloads, state, tracker, new SeedingDialer(metadata, builder.Content()), builder.Build());

        string hash = await engine.AddAsync(Request(), deadline.Token);

        await WaitUntilAsync(
            async () => (await engine.TransfersAsync(deadline.Token))[0].State == EngineState.Completed,
            deadline.Token);

        await engine.RemoveAsync(hash, deleteFiles: false, deadline.Token);

        File.Exists(Path.Combine(downloads.Path, "season", "e01.mkv")).Should().BeTrue();
    }

    private static TorrentRequest Request() => new()
    {
        Source = "http://indexer.test/some.torrent",
        DestinationFolder = "unused - the engine uses its own option",
    };

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, CancellationToken ct)
    {
        while (!await condition())
        {
            ct.ThrowIfCancellationRequested();
            await Task.Delay(25, ct);
        }
    }

    private sealed class FakeFetcher(byte[] contents) : ITorrentFileFetcher
    {
        public Task<byte[]> FetchAsync(string url, CancellationToken ct) => Task.FromResult(contents);
    }

    private sealed class FakeTracker : IPeerSource
    {
        public List<PeerEndPoint> Peers { get; } = [];

        public bool CanAnnounceTo(string url) => true;

        public Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct) =>
            Task.FromResult(new AnnounceResult(Peers, TimeSpan.FromMinutes(30)));
    }

    /// <summary>Every peer refuses, which is what a dead swarm looks like from here.</summary>
    private sealed class RefusingDialer : IPeerDialer
    {
        public Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct) =>
            throw new IOException($"nothing is listening on {peer}");
    }

    /// <summary>Puts a seeder on the other end of every dial, in this same process.</summary>
    private sealed class SeedingDialer(TorrentMetadata metadata, byte[] content) : IPeerDialer
    {
        public Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct)
        {
            (Stream ours, Stream theirs) = DuplexPair.Create();

            _ = Task.Run(async () =>
            {
                try
                {
                    await new TestSeeder(metadata, content).ServeAsync(theirs, ct);
                }
                catch
                {
                    // The engine hanging up when it is done is the normal ending here.
                }
            }, ct);

            return Task.FromResult(ours);
        }
    }
}
