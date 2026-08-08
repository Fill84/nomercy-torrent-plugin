// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Threading.Channels;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// Two streams wired to each other in one process. What one writes, the other reads.
///
/// <para>
/// This is the whole reason <c>PeerConnection</c> and the MSE handshake take a
/// <see cref="Stream"/> rather than a socket: both ends of a real conversation can be
/// driven by one test, with no port, no loopback, and no timing.
/// </para>
/// </summary>
public static class DuplexPair
{
    public static (Stream Left, Stream Right) Create()
    {
        Channel<byte[]> leftToRight = Channel.CreateUnbounded<byte[]>();
        Channel<byte[]> rightToLeft = Channel.CreateUnbounded<byte[]>();

        return (new ChannelStream(rightToLeft, leftToRight), new ChannelStream(leftToRight, rightToLeft));
    }

    private sealed class ChannelStream(Channel<byte[]> incoming, Channel<byte[]> outgoing) : Stream
    {
        private byte[] _pending = [];
        private int _consumed;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            while (_consumed >= _pending.Length)
            {
                if (!await incoming.Reader.WaitToReadAsync(ct))
                    return 0;

                if (!incoming.Reader.TryRead(out byte[]? next))
                    continue;

                _pending = next;
                _consumed = 0;
            }

            int take = Math.Min(buffer.Length, _pending.Length - _consumed);
            _pending.AsMemory(_consumed, take).CopyTo(buffer);
            _consumed += take;

            return take;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            if (!buffer.IsEmpty)
                outgoing.Writer.TryWrite(buffer.ToArray());

            return ValueTask.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Write(byte[] buffer, int offset, int count) =>
            WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        protected override void Dispose(bool disposing)
        {
            outgoing.Writer.TryComplete();
            base.Dispose(disposing);
        }

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
