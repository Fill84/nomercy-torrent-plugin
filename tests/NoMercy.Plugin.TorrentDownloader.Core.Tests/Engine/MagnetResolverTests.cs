// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Engine;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Engine;

public class MagnetResolverTests
{
    private static CancellationToken Deadline() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    private static TorrentBuilder SeasonPack() => new TorrentBuilder()
        .WithName("season")
        .WithPieceLength(4096)
        .WithFile("season/e01.mkv", new byte[9000])
        .WithFile("season/e02.mkv", new byte[7000]);

    private static string MagnetFor(TorrentBuilder builder) =>
        $"magnet:?xt=urn:btih:{Convert.ToHexStringLower(builder.ExpectedInfoHash())}" +
        "&dn=season&tr=" + Uri.EscapeDataString("http://tracker.test/announce");

    private static MagnetResolver Resolver(TorrentBuilder builder, MetadataSeeder seeder, out FakeTracker tracker)
    {
        tracker = new FakeTracker();
        tracker.Peers.Add(new PeerEndPoint(IPAddress.Parse("10.0.0.5"), 6881));

        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        return new MagnetResolver([tracker], new SeedingDialer(metadata, seeder), Handshake.NewPeerId());
    }

    [Fact]
    public async Task ResolveAsync_RebuildsTheTorrentFromAPeer()
    {
        TorrentBuilder builder = SeasonPack();
        MagnetResolver resolver = Resolver(builder, new MetadataSeeder(builder.Build()), out _);

        TorrentMetadata metadata = await resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)),
            [],
            TimeSpan.FromSeconds(20),
            Deadline());

        metadata.InfoHash.Should().Equal(builder.ExpectedInfoHash());
        metadata.Name.Should().Be("season");
        metadata.Files.Should().HaveCount(2);
        metadata.TotalLength.Should().Be(16000);
    }

    [Fact]
    public async Task ResolveAsync_KeepsTheTrackersTheMagnetAndTheIndexersNamed()
    {
        TorrentBuilder builder = SeasonPack();
        MagnetResolver resolver = Resolver(builder, new MetadataSeeder(builder.Build()), out _);

        TorrentMetadata metadata = await resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)),
            ["udp://extra.test:1337/announce"],
            TimeSpan.FromSeconds(20),
            Deadline());

        // The merged set from the aggregator has to survive the trip, or resolving a
        // magnet quietly shrinks the swarm back to whatever the link itself listed.
        metadata.Trackers.Should().Contain("http://tracker.test/announce");
        metadata.Trackers.Should().Contain("udp://extra.test:1337/announce");
    }

    [Fact]
    public async Task ResolveAsync_UsesTheIdentifierThePeerAskedFor()
    {
        TorrentBuilder builder = SeasonPack();

        // A peer picks its own number for ut_metadata. Assuming ours works with most
        // clients and fails with the rest, which is the worst kind of bug to chase.
        MagnetResolver resolver = Resolver(builder, new MetadataSeeder(builder.Build()) { OurExtensionId = 3 }, out _);

        TorrentMetadata metadata = await resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)), [], TimeSpan.FromSeconds(20), Deadline());

        metadata.Name.Should().Be("season");
    }

    [Fact]
    public async Task ResolveAsync_RefusesMetadataThatIsForADifferentTorrent()
    {
        TorrentBuilder builder = SeasonPack();

        byte[] somethingElse = MetadataSeeder.InfoDictionaryOf(new TorrentBuilder()
            .WithName("not-what-was-asked-for")
            .WithPieceLength(4096)
            .WithFile("not-what-was-asked-for/x.mkv", new byte[500])
            .Build());

        MagnetResolver resolver = Resolver(builder, new MetadataSeeder(builder.Build()) { LieWith = somethingElse }, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)), [], TimeSpan.FromSeconds(5), Deadline());

        // Without the hash check a peer hands us a different torrent and we download it
        // without ever noticing. This is the check.
        await resolve.Should().ThrowAsync<MetadataException>();
    }

    [Fact]
    public async Task ResolveAsync_GivesUpWhenThePeerRejectsEveryPiece()
    {
        TorrentBuilder builder = SeasonPack();
        MagnetResolver resolver = Resolver(builder, new MetadataSeeder(builder.Build()) { RejectEverything = true }, out _);

        Func<Task> resolve = () => resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)), [], TimeSpan.FromSeconds(5), Deadline());

        await resolve.Should().ThrowAsync<MetadataException>();
    }

    [Fact]
    public async Task ResolveAsync_GivesUpWhenNoTrackerNamesAnyPeer()
    {
        TorrentBuilder builder = SeasonPack();
        FakeTracker empty = new();
        TorrentMetadata metadata = MetadataParser.FromTorrentFile(builder.Build());

        MagnetResolver resolver = new(
            [empty],
            new SeedingDialer(metadata, new MetadataSeeder(builder.Build())),
            Handshake.NewPeerId());

        Func<Task> resolve = () => resolver.ResolveAsync(
            MagnetLink.Parse(MagnetFor(builder)), [], TimeSpan.FromSeconds(5), Deadline());

        await resolve.Should().ThrowAsync<MetadataException>();
    }

    private sealed class FakeTracker : IPeerSource
    {
        public List<PeerEndPoint> Peers { get; } = [];

        public bool CanAnnounceTo(string url) => true;

        public Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct) =>
            Task.FromResult(new AnnounceResult(Peers, TimeSpan.FromMinutes(30)));
    }

    private sealed class SeedingDialer(TorrentMetadata metadata, MetadataSeeder seeder) : IPeerDialer
    {
        public Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct)
        {
            (Stream ours, Stream theirs) = DuplexPair.Create();

            _ = Task.Run(async () =>
            {
                try
                {
                    await seeder.ServeAsync(theirs, metadata, ct);
                }
                catch
                {
                    // Hanging up once the metadata is in is the normal ending.
                }
            }, ct);

            return Task.FromResult(ours);
        }
    }
}
