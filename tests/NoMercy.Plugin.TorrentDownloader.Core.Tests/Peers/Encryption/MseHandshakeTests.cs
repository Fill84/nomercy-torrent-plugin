// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text;
using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers.Encryption;

public class MseHandshakeTests
{
    private static readonly byte[] InfoHash = Enumerable.Range(0, 20).Select(value => (byte)value).ToArray();

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(20)).Token;

    [Fact]
    public async Task Handshake_LetsBothSidesTalkAfterwards()
    {
        (Stream dialler, Stream answerer) = DuplexPair.Create();
        CancellationToken ct = Timeout();
        byte[] initialPayload = Encoding.ASCII.GetBytes("the BT handshake would go here");

        Task<Stream> initiating = MseHandshake.InitiateAsync(dialler, InfoHash, initialPayload, ct);
        Task<MseAccepted> accepting = MseHandshake.AcceptAsync(answerer, InfoHash, ct);

        Stream encrypted = await initiating;
        MseAccepted accepted = await accepting;

        // Whatever the dialler sent as its initial payload arrives intact on the far side.
        accepted.InitialPayload.Should().Equal(initialPayload);

        // And the tunnel keeps working in both directions after the handshake.
        byte[] outbound = Encoding.ASCII.GetBytes("a request");
        await encrypted.WriteAsync(outbound, ct);
        byte[] received = new byte[outbound.Length];
        await accepted.Stream.ReadExactlyAsync(received, ct);
        received.Should().Equal(outbound);

        byte[] inbound = Encoding.ASCII.GetBytes("a block!!");
        await accepted.Stream.WriteAsync(inbound, ct);
        byte[] back = new byte[inbound.Length];
        await encrypted.ReadExactlyAsync(back, ct);
        back.Should().Equal(inbound);
    }

    [Fact]
    public async Task Handshake_PutsNothingReadableOnTheWire()
    {
        (Stream dialler, Stream answerer) = DuplexPair.Create();
        CancellationToken ct = Timeout();
        RecordingStream watched = new(answerer);

        Task<Stream> initiating = MseHandshake.InitiateAsync(dialler, InfoHash, "BitTorrent protocol"u8.ToArray(), ct);
        Task<MseAccepted> accepting = MseHandshake.AcceptAsync(watched, InfoHash, ct);

        await initiating;
        await accepting;

        // The point of MSE is that equipment shaping traffic by pattern cannot see the
        // protocol name or the torrent being asked for anywhere in the bytes.
        string onTheWire = Encoding.ASCII.GetString(watched.Seen.ToArray());
        onTheWire.Should().NotContain("BitTorrent protocol");
        Convert.ToHexString(watched.Seen.ToArray()).Should().NotContain(Convert.ToHexString(InfoHash));
    }

    [Fact]
    public async Task AcceptAsync_RefusesAConnectionForAnotherTorrent()
    {
        (Stream dialler, Stream answerer) = DuplexPair.Create();
        CancellationToken ct = Timeout();
        byte[] otherTorrent = Enumerable.Repeat((byte)0xAB, 20).ToArray();

        Task<Stream> initiating = MseHandshake.InitiateAsync(dialler, otherTorrent, new byte[] { 1, 2, 3 }, ct);
        Func<Task> accepting = () => MseHandshake.AcceptAsync(answerer, InfoHash, ct);

        await accepting.Should().ThrowAsync<PeerProtocolException>();
        initiating.IsCompleted.Should().BeFalse("the dialler is still waiting, it has not been told anything");
    }

    [Fact]
    public async Task InitiateAsync_RefusesAPeerThatWillNotEncrypt()
    {
        (Stream dialler, Stream answerer) = DuplexPair.Create();
        CancellationToken ct = Timeout();

        Task<Stream> initiating = MseHandshake.InitiateAsync(dialler, InfoHash, new byte[] { 1, 2, 3 }, ct);
        Task answering = MseHandshake.AcceptAsync(answerer, InfoHash, ct, forcePlaintextForTest: true);

        // Forced encryption means a peer choosing plaintext is refused rather than
        // downgraded to. Half the point of turning it on is that it cannot be undone
        // by the other side.
        Func<Task> dial = () => initiating;
        await dial.Should().ThrowAsync<PeerProtocolException>();

        await answering.ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>Passes bytes through while keeping a copy of everything the far side sent.</summary>
    private sealed class RecordingStream(Stream inner) : Stream
    {
        public List<byte> Seen { get; } = [];

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            int read = await inner.ReadAsync(buffer, ct);
            Seen.AddRange(buffer[..read].ToArray());
            return read;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            inner.WriteAsync(buffer, ct);

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
