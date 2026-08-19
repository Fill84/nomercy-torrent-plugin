using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The one port the client listens on.
/// </summary>
/// <remarks>
/// Against real sockets on a real port. A fake here would be a second
/// implementation of exactly the part that can be wrong: whether the operating
/// system gave us the port.
/// </remarks>
public class ListenSocketsTests
{
    /// <remarks>
    /// TCP and UDP, on the same number. Peers dial in over TCP; the DHT, the
    /// UDP trackers and local discovery all speak UDP. A client that bound only
    /// one works for a fortnight and is then the reason a torrent with no HTTP
    /// tracker never finds a peer.
    /// </remarks>
    [Fact]
    public void BothProtocolsAreBoundOnTheSameNumber()
    {
        // Nought: the operating system picks one it can give both, which is
        // also how the client asks when the owner has chosen no port.
        using ListenSockets sockets = ListenSockets.Bind(0);

        Assert.Equal(sockets.Port, ((IPEndPoint)sockets.Tcp.LocalEndPoint!).Port);
        Assert.Equal(sockets.Port, ((IPEndPoint)sockets.Udp.LocalEndPoint!).Port);

        Assert.Equal(ProtocolType.Tcp, sockets.Tcp.ProtocolType);
        Assert.Equal(ProtocolType.Udp, sockets.Udp.ProtocolType);
    }

    /// <remarks>
    /// <para>
    /// Asking for any port must never fail, and letting the operating system
    /// choose is what made it fail. Its dynamic range is 49152 to 65535 and
    /// 1460 of those ports are reserved for Hyper-V and WSL in fifteen blocks a
    /// hundred wide — with the TCP set and the UDP set not the same, so a
    /// number handed out for one is refused for the other. The pool walks
    /// forward, so once it is inside a block every attempt fails together: five
    /// of these tests went down at once on 59435 to 59451.
    /// </para>
    /// <para>
    /// Three hundred, taken one after another, because that is what walks the
    /// pool far enough to meet a block. Fewer would be a test that passes
    /// against the fault it was written for.
    /// </para>
    /// </remarks>
    [Fact]
    public void AskingForAnyPortNeverFails()
    {
        for (int attempt = 0; attempt < 300; attempt++)
        {
            using ListenSockets sockets = ListenSockets.Bind(0);

            Assert.NotEqual(0, sockets.Port);
        }
    }

    /// <remarks>
    /// A port something else has is refused by its number, because the number
    /// is the only part the owner can do anything about. It is asked for once
    /// and not retried: the owner chose that number and has to be told it
    /// cannot be had, rather than being quietly given a different one.
    /// </remarks>
    [Fact]
    public void APortSomethingElseHasIsRefusedByItsNumber()
    {
        // Held for the whole test rather than sampled and let go: a port this
        // process released is one another test can take between the two lines,
        // and a flaky test about ports is worse than none.
        using ListenSockets held = ListenSockets.Bind(0);

        PortInUseException refused = Assert.Throws<PortInUseException>(() => ListenSockets.Bind(held.Port));

        Assert.Equal(held.Port, refused.Port);
        Assert.Contains(held.Port.ToString(), refused.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// And nothing half-bound is left behind when the second one fails: the
    /// port has to be free for the next attempt, including this process's own.
    /// </remarks>
    [Fact]
    public void AFailedBindLeavesThePortFree()
    {
        int port;

        using (ListenSockets held = ListenSockets.Bind(0))
        {
            port = held.Port;

            Assert.Throws<PortInUseException>(() => ListenSockets.Bind(port));
        }

        // The holder is gone, and the port is ours — which it would not be if
        // the failed attempt had left a socket of its own on it.
        using ListenSockets sockets = ListenSockets.Bind(port);

        Assert.Equal(port, sockets.Port);
    }

    /// <remarks>
    /// Closed means closed. The client is stopped and started again inside one
    /// server's life — a settings change is enough — and a socket left behind
    /// would refuse the port to its own replacement.
    /// </remarks>
    [Fact]
    public void ClosingThemLetsThePortGo()
    {
        ListenSockets first = ListenSockets.Bind(0);
        int port = first.Port;

        first.Dispose();

        using ListenSockets second = ListenSockets.Bind(port);

        Assert.Equal(port, second.Port);
    }
}
