// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Engine;

/// <summary>
/// Taking on a magnet whose swarm may not answer.
///
/// <para>
/// This is the defect that explains a fortnight of a plugin that decided to download
/// something and left no trace of it. AddAsync awaited the metadata exchange before it
/// returned anything, so a swarm with no peers meant five minutes of a blocked search cycle
/// and then a MetadataException thrown through the caller - past the point where the grab
/// would have been recorded. The owner saw no download, no failure, and no reason.
/// </para>
///
/// <para>
/// The info hash is in the magnet. Nothing has to be asked of anybody to know it.
/// </para>
/// </summary>
public class TorrentEngineResolvingTests
{
    private const string Hash = "0123456789abcdef0123456789abcdef01234567";

    private static readonly DateTimeOffset Start = new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    private static TorrentRequest Magnet(TempFolder downloads) => new()
    {
        Source = $"magnet:?xt=urn:btih:{Hash}&dn=Some.Show.S01E01",
        DestinationFolder = downloads.Path,
    };

    private static TorrentEngine Engine(TempFolder downloads, TempFolder state) =>
        new(
            [new SilentTracker()],
            new NeverAnsweringDialer(),
            new UnusedFetcher(),
            new TorrentEngineOptions
            {
                DownloadFolder = downloads.Path,
                StateFolder = state.Path,

                // Far longer than the assertions below wait. The point is that nobody waits
                // for it, so a test that passed only because the timeout was short would be
                // proving the wrong thing.
                MetadataTimeout = TimeSpan.FromMinutes(5),
            },
            () => Start);

    [Fact]
    public async Task AddAsync_AMagnetReturnsBeforeAnyPeerHasAnswered()
    {
        using TempFolder downloads = new();
        using TempFolder state = new();
        await using TorrentEngine engine = Engine(downloads, state);

        string infoHash = await engine
            .AddAsync(Magnet(downloads), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));

        infoHash.Should().Be(Hash, "the hash is in the magnet - nobody has to be asked for it");
    }

    [Fact]
    public async Task TransfersAsync_ReportsAMagnetWaitingOnItsMetadata()
    {
        using TempFolder downloads = new();
        using TempFolder state = new();
        await using TorrentEngine engine = Engine(downloads, state);

        await engine.AddAsync(Magnet(downloads), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        EngineTransfer transfer = (await engine.TransfersAsync(CancellationToken.None))
            .Should().ContainSingle().Subject;

        transfer.State.Should().Be(EngineState.Resolving);
        transfer.InfoHash.Should().Be(Hash);
    }

    /// <summary>
    /// Asked for twice is one torrent. Two episodes can want one season pack, and a retry
    /// can arrive while the first attempt is still waiting on the same silent swarm.
    /// </summary>
    [Fact]
    public async Task AddAsync_TheSameMagnetTwiceIsOneTorrent()
    {
        using TempFolder downloads = new();
        using TempFolder state = new();
        await using TorrentEngine engine = Engine(downloads, state);

        await engine.AddAsync(Magnet(downloads), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await engine.AddAsync(Magnet(downloads), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        (await engine.TransfersAsync(CancellationToken.None)).Should().ContainSingle();
    }

    /// <summary>A tracker that knows nobody, which is the swarm this actually failed against.</summary>
    private sealed class SilentTracker : IPeerSource
    {
        public bool CanAnnounceTo(string url) => true;

        public Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct) =>
            Task.FromResult(new AnnounceResult([], TimeSpan.FromMinutes(30)));
    }

    private sealed class NeverAnsweringDialer : IPeerDialer
    {
        public Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct) =>
            throw new IOException($"nothing is listening on {peer}");
    }

    /// <summary>A magnet never reaches the fetcher, and a test that says so out loud reads better than a null.</summary>
    private sealed class UnusedFetcher : ITorrentFileFetcher
    {
        public Task<byte[]> FetchAsync(string url, CancellationToken ct) =>
            throw new InvalidOperationException("a magnet must not be fetched over HTTP");
    }
}
