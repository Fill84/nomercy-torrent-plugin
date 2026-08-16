using System.Net;
using System.Net.Sockets;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// A port the client cannot have.
/// </summary>
/// <remarks>
/// <para>
/// Named with the number, because "the port is in use" is not something anybody
/// can act on and "51413 is in use" is: the owner changes it or stops whatever
/// has it.
/// </para>
/// <para>
/// And named with which refusal it was. Windows reserves ranges of ports for
/// Hyper-V and WSL and refuses those with a permission error rather than an
/// in-use one — measured here, on a port that bound for TCP and refused for
/// UDP. "Something else has it" would send the owner looking for a program
/// that does not exist.
/// </para>
/// </remarks>
public sealed class PortInUseException : Exception
{
    public PortInUseException(int port, SocketException? cause = null)
        : base(Why(port, cause), cause)
    {
        Port = port;
    }

    public int Port { get; }

    private static string Why(int port, SocketException? cause)
    {
        return cause?.SocketErrorCode switch
        {
            SocketError.AddressAlreadyInUse =>
                $"Port {port} is already in use, so the torrent client cannot listen on it.",
            SocketError.AccessDenied =>
                $"Port {port} is one this machine will not allow, so the torrent client cannot listen on it. "
                + "Windows reserves ranges of ports for other things; another number will work.",
            _ =>
                $"Port {port} could not be listened on: {cause?.SocketErrorCode.ToString() ?? "no reason given"}.",
        };
    }
}

/// <summary>
/// The one port this client listens on, TCP and UDP together.
/// </summary>
/// <remarks>
/// <para>
/// Both, and the same number: peers dial in over TCP, and the DHT, UDP trackers
/// and local discovery all speak UDP. A client that bound only one would work
/// for a fortnight and then be the reason a torrent with no HTTP tracker never
/// found a peer.
/// </para>
/// <para>
/// Bound together so that a half-bound state cannot exist. If the second one
/// fails, the first is closed before anything is told the port is ours.
/// </para>
/// </remarks>
public sealed class ListenSockets : IDisposable
{
    private ListenSockets(Socket tcp, Socket udp, int port)
    {
        Tcp = tcp;
        Udp = udp;
        Port = port;
    }

    /// <summary>Where peers dial in.</summary>
    public Socket Tcp { get; }

    /// <summary>The DHT, the UDP trackers and local discovery.</summary>
    public Socket Udp { get; }

    /// <summary>The number both are on.</summary>
    public int Port { get; }

    /// <summary>
    /// Binds <paramref name="port"/> for both, or says who has it.
    /// </summary>
    /// <exception cref="PortInUseException">Something else is on that port.</exception>
    public static ListenSockets Bind(int port)
    {
        // UDP first, and that order is not arbitrary. Windows reserves whole
        // ranges of ports for Hyper-V and WSL and refuses them for UDP while
        // handing the same numbers out for TCP, so asking TCP to choose gives a
        // number UDP often cannot have. Asking UDP to choose gives one both can.
        Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Socket? tcp = null;

        try
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, port));

            // The number UDP really got: port 0 means "anything free", and the
            // two have to agree or a peer told one number would announce on
            // another.
            int bound = ((IPEndPoint)udp.LocalEndPoint!).Port;

            tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            tcp.Bind(new IPEndPoint(IPAddress.Any, bound));
            tcp.Listen(backlog: 64);

            return new(tcp, udp, bound);
        }
        catch (SocketException cause)
        {
            // Nothing half-bound is handed back: whichever of the two succeeded
            // is closed here, or the next attempt on the same port fails
            // against this process itself.
            tcp?.Dispose();
            udp.Dispose();

            throw new PortInUseException(port, cause);
        }
        catch
        {
            tcp?.Dispose();
            udp.Dispose();

            throw;
        }
    }

    public void Dispose()
    {
        Tcp.Dispose();
        Udp.Dispose();
    }
}
