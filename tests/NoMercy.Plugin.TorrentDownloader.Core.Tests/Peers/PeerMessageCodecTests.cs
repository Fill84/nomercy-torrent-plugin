// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Core.Peers;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Peers;

public class PeerMessageCodecTests
{
    private static async Task<PeerMessage> RoundTripAsync(PeerMessage message)
    {
        MemoryStream buffer = new(PeerMessageCodec.Write(message));
        return await PeerMessageCodec.ReadAsync(buffer, CancellationToken.None);
    }

    public static TheoryData<PeerMessage> EveryMessage() =>
    [
        new KeepAlive(),
        new Choke(),
        new Unchoke(),
        new Interested(),
        new NotInterested(),
        new Have(7),
        new BitfieldMessage([0b1010_0000]),
        new Request(1, 16384, 16384),
        new PieceBlock(2, 32768, [1, 2, 3, 4]),
        new Cancel(3, 0, 16384),
        new Port(6881),
        new Extended(1, [9, 9]),
    ];

    [Theory]
    [MemberData(nameof(EveryMessage))]
    public async Task WriteThenRead_ReturnsAnEqualMessage(PeerMessage message)
    {
        PeerMessage read = await RoundTripAsync(message);

        read.Should().Be(message);
    }

    [Fact]
    public async Task ReadAsync_DecodesAMessageThatArrivesOneByteAtATime()
    {
        PieceBlock original = new(4, 16384, [7, 7, 7, 7, 7, 7, 7, 7]);
        DribbleStream dribble = new(PeerMessageCodec.Write(original));

        PeerMessage read = await PeerMessageCodec.ReadAsync(dribble, CancellationToken.None);

        read.Should().Be(original);
    }

    [Fact]
    public async Task ReadAsync_ReadsSeveralMessagesFromOneStreamInOrder()
    {
        MemoryStream buffer = new();
        buffer.Write(PeerMessageCodec.Write(new Interested()));
        buffer.Write(PeerMessageCodec.Write(new Have(3)));
        buffer.Write(PeerMessageCodec.Write(new KeepAlive()));
        buffer.Position = 0;

        (await PeerMessageCodec.ReadAsync(buffer, CancellationToken.None)).Should().BeOfType<Interested>();
        (await PeerMessageCodec.ReadAsync(buffer, CancellationToken.None)).Should().BeOfType<Have>()
            .Which.PieceIndex.Should().Be(3);
        (await PeerMessageCodec.ReadAsync(buffer, CancellationToken.None)).Should().BeOfType<KeepAlive>();
    }

    [Fact]
    public void Write_PrefixesEveryMessageWithItsBigEndianLength()
    {
        byte[] written = PeerMessageCodec.Write(new Have(1));

        // 4-byte length of 5, then id 4, then the piece index.
        written.Should().Equal((byte)0, (byte)0, (byte)0, (byte)5, (byte)4, (byte)0, (byte)0, (byte)0, (byte)1);
    }

    [Fact]
    public void Write_GivesKeepAliveAZeroLengthAndNoBody()
    {
        PeerMessageCodec.Write(new KeepAlive()).Should().Equal((byte)0, (byte)0, (byte)0, (byte)0);
    }

    [Fact]
    public async Task ReadAsync_RefusesAMessageLongerThanAnyPeerShouldSend()
    {
        // A peer claiming a 64 MB message is either broken or trying to make us
        // allocate on command. Refuse before the buffer is reserved.
        MemoryStream buffer = new([0x04, 0x00, 0x00, 0x00, 0x05]);

        Func<Task> read = () => PeerMessageCodec.ReadAsync(buffer, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>();
    }

    [Fact]
    public async Task ReadAsync_RefusesAnUnknownMessageId()
    {
        MemoryStream buffer = new([0x00, 0x00, 0x00, 0x01, 0x63]);

        Func<Task> read = () => PeerMessageCodec.ReadAsync(buffer, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>();
    }

    [Fact]
    public async Task ReadAsync_RefusesAMessageWhoseLengthDoesNotFitItsId()
    {
        // "have" is always five bytes. Anything else is not a have.
        MemoryStream buffer = new([0x00, 0x00, 0x00, 0x06, 0x04, 0x00, 0x00, 0x00, 0x01, 0x00]);

        Func<Task> read = () => PeerMessageCodec.ReadAsync(buffer, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<PeerProtocolException>();
    }

    [Fact]
    public async Task ReadAsync_RefusesAStreamThatEndsMidMessage()
    {
        MemoryStream buffer = new([0x00, 0x00, 0x00, 0x05, 0x04, 0x00]);

        Func<Task> read = () => PeerMessageCodec.ReadAsync(buffer, CancellationToken.None).AsTask();

        await read.Should().ThrowAsync<EndOfStreamException>();
    }

    /// <summary>A stream that hands over one byte per read, the way a slow socket does.</summary>
    private sealed class DribbleStream(byte[] contents) : Stream
    {
        private int _position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= contents.Length || count == 0)
                return 0;

            buffer[offset] = contents[_position++];
            return 1;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => contents.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
