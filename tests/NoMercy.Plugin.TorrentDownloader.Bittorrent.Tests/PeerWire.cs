using System.IO.Pipelines;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Two ends of a connection, in memory, with everything the dialling end sent
/// kept so a test can look at the wire itself.
/// </summary>
/// <remarks>
/// A real socket would do, and would make these tests depend on a network. What
/// matters is that the two ends are separate and that bytes arrive in the order
/// and the sizes they were written in — a pipe does both.
/// </remarks>
public sealed class PeerWire
{
    private readonly MemoryStream _sent = new();
    private readonly Lock _lock = new();

    public PeerWire()
    {
        Pipe dialled = new();
        Pipe answered = new();

        Initiator = new Duplex(answered.Reader.AsStream(), dialled.Writer.AsStream(), this);
        Receiver = new Duplex(dialled.Reader.AsStream(), answered.Writer.AsStream(), owner: null);
    }

    /// <summary>The end that dials.</summary>
    public Stream Initiator { get; }

    /// <summary>The end that answers.</summary>
    public Stream Receiver { get; }

    /// <summary>Every byte the dialling end put on the wire.</summary>
    public byte[] Sent
    {
        get
        {
            lock (_lock)
            {
                return _sent.ToArray();
            }
        }
    }

    private void Record(ReadOnlySpan<byte> bytes)
    {
        lock (_lock)
        {
            _sent.Write(bytes);
        }
    }

    private sealed class Duplex(Stream reading, Stream writing, PeerWire? owner) : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            return reading.Read(buffer, offset, count);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            return reading.ReadAsync(buffer, ct);
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            owner?.Record(buffer.AsSpan(offset, count));
            writing.Write(buffer, offset, count);
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            owner?.Record(buffer.Span);

            await writing.WriteAsync(buffer, ct);
        }

        public override void Flush()
        {
            writing.Flush();
        }

        public override Task FlushAsync(CancellationToken ct)
        {
            return writing.FlushAsync(ct);
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotSupportedException();
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        /// <summary>
        /// Hangs up, as a socket does.
        /// </summary>
        /// <remarks>
        /// Disposing the writing end completes the pipe, so the far side's next
        /// read answers nought rather than waiting for ever. Without it a test
        /// that watches a peer being dropped waits on a connection nothing will
        /// ever close.
        /// </remarks>
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writing.Dispose();
                reading.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
