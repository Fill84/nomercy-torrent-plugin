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
    /// How many numbers are tried when the caller wants any port.
    /// </summary>
    /// <remarks>
    /// Each attempt is an independent draw, so eight of them make a failure
    /// vanishingly unlikely. Consecutive numbers would not: the operating
    /// system's own ephemeral pool walks forward, and eight tries inside a
    /// reserved block a hundred ports wide are eight failures — measured here
    /// as five tests failing together on 59435 to 59451.
    /// </remarks>
    private const int Attempts = 8;

    /// <summary>
    /// Where a port is drawn from when the caller wants any.
    /// </summary>
    /// <remarks>
    /// Below the operating system's own dynamic range, deliberately. Measured
    /// on this machine: the dynamic range is 49152 to 65535, and 1460 of those
    /// ports are reserved for Hyper-V and WSL in fifteen blocks — with the TCP
    /// set and the UDP set not the same, so a number handed out for one is
    /// refused for the other. Below 49152 nothing is excluded at all. Above
    /// 20000 keeps clear of the registered ports that other software expects to
    /// find free.
    /// </remarks>
    private const int LowestDrawn = 20000;

    /// <summary>The top of that range, exclusive.</summary>
    private const int HighestDrawn = 48000;

    /// <summary>
    /// Binds <paramref name="port"/> for both, or says who has it.
    /// </summary>
    /// <remarks>
    /// A number the owner chose is asked for once and refused by its number:
    /// they have forwarded it by hand, and quietly listening on a different one
    /// would be a forwarded port with nothing behind it. Nought means any, and
    /// then this client draws the number itself rather than letting the
    /// operating system choose from a range where a third of the blocks are
    /// reserved.
    /// </remarks>
    /// <exception cref="PortInUseException">Something else is on that port.</exception>
    public static ListenSockets Bind(int port)
    {
        PortInUseException? refused = null;

        for (int attempt = 0; attempt < (port == 0 ? Attempts : 1); attempt++)
        {
            try
            {
                return Once(port == 0 ? Random.Shared.Next(LowestDrawn, HighestDrawn) : port);
            }
            catch (PortInUseException taken)
            {
                refused = taken;
            }
        }

        throw refused!;
    }

    /// <summary>One attempt at one number, which is never nought.</summary>
    /// <remarks>
    /// UDP first, and that order is not arbitrary: Windows refuses reserved
    /// ranges for UDP while handing the same numbers out for TCP, so a TCP bind
    /// that succeeded would say the port is ours when it is not.
    /// </remarks>
    private static ListenSockets Once(int port)
    {
        Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

        try
        {
            udp.Bind(new IPEndPoint(IPAddress.Any, port));
            tcp.Bind(new IPEndPoint(IPAddress.Any, port));
            tcp.Listen(backlog: 64);

            return new(tcp, udp, port);
        }
        catch (SocketException cause)
        {
            // Nothing half-bound is handed back: whichever of the two succeeded
            // is closed here, or the next attempt on the same port fails
            // against this process itself.
            tcp.Dispose();
            udp.Dispose();

            throw new PortInUseException(port, cause);
        }
        catch
        {
            tcp.Dispose();
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
