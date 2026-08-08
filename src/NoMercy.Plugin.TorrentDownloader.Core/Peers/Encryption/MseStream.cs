// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;

/// <summary>
/// The negotiated tunnel. Everything written is enciphered, everything read is
/// deciphered, and the layer above it never learns that MSE happened.
/// </summary>
public sealed class MseStream(Stream inner, Rc4Engine encryptor, Rc4Engine decryptor) : Stream
{
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        int read = await inner.ReadAsync(buffer, ct);

        if (read > 0)
            decryptor.Process(buffer.Span[..read]);

        return read;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        // RC4 is a stream cipher, so the keystream position must advance in exactly the
        // order bytes leave. Copy rather than encrypt the caller's buffer in place: it
        // may be reused, and handing back ciphertext it did not ask for is a nasty bug.
        byte[] enciphered = buffer.ToArray();
        encryptor.Process(enciphered);

        await inner.WriteAsync(enciphered, ct);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override void Write(byte[] buffer, int offset, int count) =>
        WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override void Flush() => inner.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();

        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
