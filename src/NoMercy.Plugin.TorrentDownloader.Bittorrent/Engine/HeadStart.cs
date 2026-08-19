namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// A stream with some bytes already in hand.
/// </summary>
/// <remarks>
/// The encryption negotiation reads ahead — it has to, because neither end
/// knows how much padding the other sent — so by the time there is a connection
/// to make, the peer's first message has often been read off the wire already.
/// Handing that to whatever reads next is the difference between a peer whose
/// bitfield arrived early and a peer believed to have nothing.
/// </remarks>
public sealed class HeadStart(Stream inner, ReadOnlyMemory<byte> head) : Stream
{
    private int _taken;

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => false;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int taken = Take(buffer.AsSpan(offset, count));

        return taken > 0 ? taken : inner.Read(buffer, offset, count);
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int taken = Take(buffer.Span);

        // The head first and never mixed with the wire in one read: a caller
        // that asked for more than is in hand gets what is in hand, and asks
        // again. Reading past it in the same call would block on a peer that
        // has said everything it means to say for now.
        return taken > 0 ? taken : await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        inner.Write(buffer, offset, count);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        return inner.WriteAsync(buffer, ct);
    }

    public override void Flush()
    {
        inner.Flush();
    }

    public override Task FlushAsync(CancellationToken ct)
    {
        return inner.FlushAsync(ct);
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        throw new NotSupportedException();
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>How much of the head this read can have, which may be none.</summary>
    private int Take(Span<byte> buffer)
    {
        int left = head.Length - _taken;

        if (left <= 0 || buffer.Length == 0)
        {
            return 0;
        }

        int taking = Math.Min(left, buffer.Length);

        head.Span.Slice(_taken, taking).CopyTo(buffer);
        _taken += taking;

        return taking;
    }
}
