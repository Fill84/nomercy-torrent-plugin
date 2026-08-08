// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Net.Sockets;

namespace NoMercy.Plugin.TorrentDownloader.Core.Trackers;

/// <summary>
/// The real socket behind <see cref="IUdpTransport"/>.
///
/// <para>
/// Deliberately thin, and the only part of the UDP tracker with no unit test: it is
/// an adapter over the operating system, and everything worth proving about the
/// protocol lives in <see cref="UdpTracker"/> where a fake transport can reach it.
/// </para>
/// </summary>
public sealed class SocketUdpTransport(TimeSpan? timeout = null, int attempts = 3) : IUdpTransport
{
    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public async Task<byte[]> ExchangeAsync(string host, int port, byte[] request, CancellationToken ct)
    {
        Exception? lastFailure = null;

        for (int attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
                return await SendOnceAsync(host, port, request, ct);
            }
            catch (Exception failure) when (failure is SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // UDP loses datagrams without telling anyone, so a silence is not an
                // answer - it is a reason to ask again before giving up on a tracker.
                lastFailure = failure;
            }
        }

        throw new TrackerException($"{host}:{port} did not answer after {attempts} attempts: {lastFailure?.Message}");
    }

    private async Task<byte[]> SendOnceAsync(string host, int port, byte[] request, CancellationToken ct)
    {
        using UdpClient client = new();
        client.Connect(host, port);

        await client.SendAsync(request, ct);

        using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
        attempt.CancelAfter(_timeout);

        UdpReceiveResult received = await client.ReceiveAsync(attempt.Token);

        return received.Buffer;
    }
}
