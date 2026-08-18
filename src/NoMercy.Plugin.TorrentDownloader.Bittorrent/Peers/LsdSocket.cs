using System.Net;
using System.Net.Sockets;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The multicast group itself: sending announces and hearing other clients'.
/// </summary>
/// <remarks>
/// <para>
/// The port is shared. Every BitTorrent client on the machine listens on the
/// same one, so the socket is opened with address reuse — without it, a second
/// client starting up either fails or takes the group away from the first, and
/// on a media server the other client is very often the one the owner uses.
/// </para>
/// <para>
/// Announces are sent with a time to live of one, so the packet reaches this
/// network and no further. That is what local discovery means, and a router
/// that forwarded it would be leaking a list of what is being downloaded to
/// whatever is on the other side.
/// </para>
/// </remarks>
public sealed class LsdSocket : IDisposable
{
    private readonly UdpClient _socket;
    private readonly IPEndPoint _group;

    public LsdSocket()
    {
        _group = new(LocalDiscovery.Group, LocalDiscovery.GroupPort);

        _socket = new();
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, LocalDiscovery.GroupPort));

        _socket.JoinMulticastGroup(LocalDiscovery.Group, timeToLive: 1);

        // Our own packets come back to us. They are wanted: a client that could
        // not hear the group it is shouting on has no way of knowing whether
        // multicast works here at all, and the cookie is what tells its own
        // announce from everybody else's.
        _socket.MulticastLoopback = true;
    }

    /// <summary>How far an announce travels, which is one hop.</summary>
    public int MulticastTimeToLive =>
        (int)_socket.Client.GetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive)!;

    /// <summary>Says what this client has, to whoever is listening.</summary>
    public async Task AnnounceAsync(int port, IEnumerable<string> infoHashes, string cookie, CancellationToken ct)
    {
        string[] hashes = [.. infoHashes];

        if (hashes.Length == 0)
        {
            // Nothing to say. An announce with no torrent in it is a packet
            // every client on the network reads for nothing.
            return;
        }

        byte[] message = LocalDiscovery.Write(port, hashes, cookie);

        await _socket.SendAsync(message, _group, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The next announce from somebody else, ignoring our own.
    /// </summary>
    /// <param name="ours">This client's cookie.</param>
    /// <param name="ct">Cancellation, which is how a caller stops listening.</param>
    public async Task<(LsdAnnounce Announce, IPAddress From)> ReceiveAsync(string ours, CancellationToken ct)
    {
        while (true)
        {
            UdpReceiveResult packet = await _socket.ReceiveAsync(ct).ConfigureAwait(false);

            if (LocalDiscovery.Read(packet.Buffer, ours) is LsdAnnounce announce)
            {
                // The address is the sender's, never anything in the message:
                // the announce says a port and nothing else, and a client that
                // trusted an address inside it could be pointed anywhere.
                return (announce, packet.RemoteEndPoint.Address);
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _socket.DropMulticastGroup(LocalDiscovery.Group);
        }
        catch (SocketException)
        {
            // Leaving a group this socket never managed to join, which is what
            // happens on a machine with no multicast route. Nothing to do about
            // it and nothing worth failing a shutdown for.
        }

        _socket.Dispose();
    }
}
