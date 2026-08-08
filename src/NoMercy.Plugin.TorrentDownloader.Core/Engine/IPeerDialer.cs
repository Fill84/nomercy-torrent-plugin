// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net.Sockets;
using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Engine;

/// <summary>
/// Opening a connection to a peer.
///
/// <para>
/// Behind an interface for the same reason <c>PeerConnection</c> takes a
/// <see cref="Stream"/>: it is what lets the engine's own behaviour - who to dial, how
/// many at once, what to do when one refuses - be proved without a socket.
/// </para>
/// </summary>
public interface IPeerDialer
{
    Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct);
}

/// <summary>
/// The real socket. Thin on purpose, and the one part of the dial path with no unit
/// test, because everything worth proving lives above it.
/// </summary>
public sealed class TcpPeerDialer(TimeSpan? timeout = null) : IPeerDialer
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<Stream> ConnectAsync(PeerEndPoint peer, CancellationToken ct)
    {
        TcpClient client = new();

        try
        {
            using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            attempt.CancelAfter(_timeout);

            await client.ConnectAsync(peer.Address, peer.Port, attempt.Token);

            // Nagle batches small writes, and every request we send is small. Waiting to
            // fill a segment adds latency to exactly the messages that ask for data.
            client.NoDelay = true;

            return client.GetStream();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
