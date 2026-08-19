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
    /// Eight, alternating which protocol chooses. Measured here: with UDP
    /// choosing every time, eight consecutive attempts were all refused on
    /// ports 50379 to 50387 — a Windows reserved range that the UDP ephemeral
    /// pool was walking through at the time. Retrying inside a block that wide
    /// never escapes it; changing which protocol picks does, because the one
    /// that picks is never handed a number out of its own reserved range.
    /// </remarks>
    private const int Attempts = 8;

    /// <summary>
    /// Binds <paramref name="port"/> for both, or says who has it.
    /// </summary>
    /// <remarks>
    /// Nought is asked for several times over; a number the owner chose is
    /// asked for once. Another ephemeral number is as good as the first, so
    /// trying again is free — but a number the owner chose has no substitute,
    /// and quietly listening on a different one would be a port they forwarded
    /// by hand with nothing behind it.
    /// </remarks>
    /// <exception cref="PortInUseException">Something else is on that port.</exception>
    public static ListenSockets Bind(int port)
    {
        PortInUseException? refused = null;

        for (int attempt = 0; attempt < (port == 0 ? Attempts : 1); attempt++)
        {
            try
            {
                // Turn and turn about. Windows reserves whole ranges of ports,
                // separately for each protocol, and a range excluded for TCP is
                // one the UDP ephemeral pool walks straight through — so eight
                // tries with UDP always choosing fail together. Whichever
                // protocol picks is never given a number from its own excluded
                // range, so alternating escapes a block that retrying cannot.
                return Once(port, udpChooses: attempt % 2 == 0);
            }
            catch (PortInUseException taken)
            {
                refused = taken;
            }
        }

        throw refused!;
    }

    /// <summary>One attempt, with one of the two picking the number.</summary>
    /// <param name="port">The number wanted, or nought for whichever is free.</param>
    /// <param name="udpChooses">
    /// Which protocol binds first and so decides the number. UDP for a port the
    /// owner named, always: Windows refuses reserved ranges for UDP while
    /// handing the same numbers out for TCP, so a TCP bind that succeeded would
    /// say the port is ours when it is not.
    /// </param>
    private static ListenSockets Once(int port, bool udpChooses)
    {
        Socket udp = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        Socket tcp = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        Socket chooser = udpChooses ? udp : tcp;
        Socket follower = udpChooses ? tcp : udp;

        // The number that really failed, which for a request for any port is
        // not the number that was asked for. "Port 0 is already in use" is a
        // sentence nobody can act on, and naming the number is the whole reason
        // this exception exists.
        int wanted = port;

        try
        {
            chooser.Bind(new IPEndPoint(IPAddress.Any, port));

            // The number it really got: port nought means "anything free", and
            // the two have to agree or a peer told one number would announce on
            // another.
            int bound = ((IPEndPoint)chooser.LocalEndPoint!).Port;

            wanted = bound;

            follower.Bind(new IPEndPoint(IPAddress.Any, bound));
            tcp.Listen(backlog: 64);

            return new(tcp, udp, bound);
        }
        catch (SocketException cause)
        {
            // Nothing half-bound is handed back: whichever of the two succeeded
            // is closed here, or the next attempt on the same port fails
            // against this process itself.
            tcp.Dispose();
            udp.Dispose();

            throw new PortInUseException(wanted, cause);
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
