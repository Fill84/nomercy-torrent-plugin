// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Core.Trackers;

/// <summary>
/// One datagram out, one datagram back. Behind an interface so the tracker's protocol
/// can be proved without a socket, a port, or a tracker that happens to be up.
/// </summary>
public interface IUdpTransport
{
    Task<byte[]> ExchangeAsync(string host, int port, byte[] request, CancellationToken ct);
}

/// <summary>
/// BEP 15. Most public trackers speak this and not HTTP, so a client without it
/// reaches a fraction of the swarms it could.
/// </summary>
public sealed class UdpTracker(IUdpTransport transport) : IPeerSource
{
    /// <summary>The fixed value every client sends to open a conversation.</summary>
    private const long ProtocolMagic = 0x41727101980;

    private const int ActionConnect = 0;
    private const int ActionAnnounce = 1;
    private const int ActionError = 3;

    private const int ConnectResponseLength = 16;
    private const int AnnounceHeaderLength = 20;
    private const int CompactEntryLength = 6;

    public bool CanAnnounceTo(string url) => url.StartsWith("udp://", StringComparison.OrdinalIgnoreCase);

    public async Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) || parsed.Scheme != "udp")
            throw new TrackerException($"'{url}' is not a UDP tracker address");

        int port = parsed.IsDefaultPort ? 80 : parsed.Port;

        // The connection id expires after a couple of minutes, so it is fetched per
        // announce rather than cached. A stale id gets every announce rejected, and
        // the extra round trip is cheaper than working that out at runtime.
        long connectionId = await ConnectAsync(parsed.Host, port, ct);

        return await AnnounceAsync(parsed.Host, port, connectionId, request, ct);
    }

    private async Task<long> ConnectAsync(string host, int port, CancellationToken ct)
    {
        int transaction = NewTransactionId();

        byte[] message = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(message, ProtocolMagic);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(8), ActionConnect);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), transaction);

        byte[] response = await ExchangeAsync(host, port, message, ct);

        if (response.Length < ConnectResponseLength)
            throw new TrackerException($"{host} answered a connect with {response.Length} bytes");

        Check(response, transaction, ActionConnect, host);

        return BinaryPrimitives.ReadInt64BigEndian(response.AsSpan(8));
    }

    private async Task<AnnounceResult> AnnounceAsync(
        string host,
        int port,
        long connectionId,
        AnnounceRequest request,
        CancellationToken ct)
    {
        int transaction = NewTransactionId();

        byte[] message = new byte[98];
        BinaryPrimitives.WriteInt64BigEndian(message, connectionId);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(8), ActionAnnounce);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(12), transaction);
        request.InfoHash.CopyTo(message, 16);
        request.PeerId.CopyTo(message, 36);
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(56), request.Downloaded);
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(64), request.Left);
        BinaryPrimitives.WriteInt64BigEndian(message.AsSpan(72), request.Uploaded);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(80), WireEvent(request.Event));

        // IP zero means "use the address this datagram came from", which is right
        // behind any NAT. The key lets a tracker recognise us across address changes.
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(84), 0);
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(88), NewTransactionId());
        BinaryPrimitives.WriteInt32BigEndian(message.AsSpan(92), -1);
        BinaryPrimitives.WriteUInt16BigEndian(message.AsSpan(96), (ushort)request.Port);

        byte[] response = await ExchangeAsync(host, port, message, ct);

        if (response.Length < AnnounceHeaderLength)
            throw new TrackerException($"{host} answered an announce with {response.Length} bytes");

        Check(response, transaction, ActionAnnounce, host);

        TimeSpan interval = TimeSpan.FromSeconds(BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(8)));

        return new AnnounceResult(ReadPeers(response), interval);
    }

    private static IReadOnlyList<PeerEndPoint> ReadPeers(byte[] response)
    {
        List<PeerEndPoint> peers = [];

        for (int offset = AnnounceHeaderLength; offset + CompactEntryLength <= response.Length; offset += CompactEntryLength)
        {
            IPAddress address = new(response.AsSpan(offset, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(offset + 4, 2));

            if (port > 0)
                peers.Add(new PeerEndPoint(address, port));
        }

        return peers;
    }

    /// <summary>
    /// The transaction id is the only thing tying a UDP reply to our request. There is
    /// no connection to trust, so a mismatch is somebody else's packet - or somebody
    /// guessing - and the answer is not ours to believe.
    /// </summary>
    private static void Check(byte[] response, int transaction, int expectedAction, string host)
    {
        int action = BinaryPrimitives.ReadInt32BigEndian(response);

        if (BinaryPrimitives.ReadInt32BigEndian(response.AsSpan(4)) != transaction)
            throw new TrackerException($"{host} answered with a different transaction id");

        if (action == ActionError)
        {
            string message = Encoding.ASCII.GetString(response, 8, response.Length - 8);
            throw new TrackerException($"{host} refused: {message}");
        }

        if (action != expectedAction)
            throw new TrackerException($"{host} answered action {action} where {expectedAction} was expected");
    }

    private async Task<byte[]> ExchangeAsync(string host, int port, byte[] message, CancellationToken ct)
    {
        try
        {
            return await transport.ExchangeAsync(host, port, message, ct);
        }
        catch (Exception failure) when (failure is SocketException or IOException)
        {
            throw new TrackerException($"{host}:{port} could not be reached: {failure.Message}");
        }
    }

    private static int NewTransactionId() => RandomNumberGenerator.GetInt32(int.MinValue, int.MaxValue);

    /// <summary>
    /// BEP 15 numbers the events differently from the order they read in: completed is
    /// 1 and started is 2. Casting the enum would compile, announce the wrong event to
    /// every UDP tracker, and never say so.
    /// </summary>
    private static int WireEvent(AnnounceEvent announceEvent) => announceEvent switch
    {
        AnnounceEvent.None => 0,
        AnnounceEvent.Completed => 1,
        AnnounceEvent.Started => 2,
        AnnounceEvent.Stopped => 3,
        _ => 0,
    };
}
