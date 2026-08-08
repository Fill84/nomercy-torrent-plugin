// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Peers.Encryption;
using NoMercy.Plugin.TorrentDownloader.Core.Torrents;

namespace NoMercy.Plugin.TorrentDownloader.Core.Peers;

/// <summary>
/// One conversation with one peer: MSE, then the BitTorrent handshake, then messages.
///
/// <para>
/// It takes a <see cref="Stream"/> rather than a socket, so a test drives both ends in
/// one process. It owns nothing but itself - no bitfield, no piece state, no opinion
/// about what to ask for. Those belong to the coordinator, which is the only owner.
/// </para>
/// </summary>
public sealed class PeerConnection : IAsyncDisposable
{
    private readonly Stream _stream;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    private PeerConnection(Stream stream, byte[] localPeerId, Handshake remote)
    {
        _stream = stream;
        LocalPeerId = localPeerId;
        RemotePeerId = remote.PeerId;
        SupportsExtensionProtocol = remote.SupportsExtensionProtocol;
    }

    public byte[] LocalPeerId { get; }

    public byte[] RemotePeerId { get; }

    public bool SupportsExtensionProtocol { get; }

    /// <summary>
    /// Dials out. The BitTorrent handshake rides along as MSE's initial payload, which
    /// is what that field is for and saves a round trip on every connection.
    /// </summary>
    public static Task<PeerConnection> DialAsync(
        Stream raw,
        TorrentMetadata metadata,
        byte[] localPeerId,
        CancellationToken ct) =>
        DialAsync(raw, metadata.InfoHash, localPeerId, ct);

    /// <summary>
    /// Dials knowing only which torrent this is. A magnet has no metadata until the
    /// peers hand it over, and the handshake never needed more than the info hash.
    /// </summary>
    public static async Task<PeerConnection> DialAsync(
        Stream raw,
        byte[] infoHash,
        byte[] localPeerId,
        CancellationToken ct)
    {
        byte[] ours = Handshake.Write(infoHash, localPeerId);
        Stream encrypted = await MseHandshake.InitiateAsync(raw, infoHash, ours, ct);

        Handshake remote = await Handshake.ReadAsync(encrypted, infoHash, ct);

        return new PeerConnection(encrypted, localPeerId, remote);
    }

    public static async Task<PeerConnection> AcceptAsync(
        Stream raw,
        TorrentMetadata metadata,
        byte[] localPeerId,
        CancellationToken ct)
    {
        MseAccepted accepted = await MseHandshake.AcceptAsync(raw, metadata.InfoHash, ct);

        // Their handshake arrived inside the MSE payload rather than on the stream.
        using MemoryStream carried = new(accepted.InitialPayload);
        Handshake remote = await Handshake.ReadAsync(carried, metadata.InfoHash, ct);

        await accepted.Stream.WriteAsync(Handshake.Write(metadata.InfoHash, localPeerId), ct);
        await accepted.Stream.FlushAsync(ct);

        return new PeerConnection(accepted.Stream, localPeerId, remote);
    }

    /// <summary>
    /// Serialised, because RC4 is a stream cipher and two interleaved writes would
    /// corrupt both messages. The coordinator has several reasons to send at once and
    /// should not have to know that.
    /// </summary>
    public async Task SendAsync(PeerMessage message, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _writeLock.WaitAsync(ct);

        try
        {
            await _stream.WriteAsync(PeerMessageCodec.Write(message), ct);
            await _stream.FlushAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>One reader only. The coordinator runs a single receive loop per peer.</summary>
    public ValueTask<PeerMessage> ReceiveAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        return PeerMessageCodec.ReadAsync(_stream, ct);
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
            return ValueTask.CompletedTask;

        _disposed = true;
        _writeLock.Dispose();

        return _stream.DisposeAsync();
    }
}
