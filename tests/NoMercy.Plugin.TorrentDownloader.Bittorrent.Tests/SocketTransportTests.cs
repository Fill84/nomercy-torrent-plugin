using System.Net;
using System.Net.Sockets;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The parts that need a socket.
/// </summary>
/// <remarks>
/// Over the loopback, against a responder in this process. Everything that
/// decides anything is above these and is tested without a network; what is
/// left is whether a datagram goes out and comes back, and whether a peer that
/// is not there is a peer that will not talk rather than an exception.
/// </remarks>
public class SocketTransportTests
{
    [Fact]
    public async Task ADatagramGoesOutAndTheAnswerComesBack()
    {
        using UdpClient tracker = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)tracker.Client.LocalEndPoint!).Port;

        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));

        Task<UdpReceiveResult> asked = tracker.ReceiveAsync(stopping.Token).AsTask();

        Task<byte[]> exchange = new SocketTrackerTransport(new HttpClient()).ExchangeAsync(
            IPAddress.Loopback.ToString(),
            port,
            [1, 2, 3, 4],
            TimeSpan.FromSeconds(10),
            stopping.Token);

        UdpReceiveResult request = await asked;

        Assert.Equal([1, 2, 3, 4], request.Buffer);

        await tracker.SendAsync(new byte[] { 9, 8, 7 }, request.RemoteEndPoint, stopping.Token);

        Assert.Equal([9, 8, 7], await exchange);
    }

    /// <remarks>
    /// A tracker that takes the datagram and says nothing is named, with how
    /// long it was given. BEP 15 says to try again with a longer wait, and that
    /// decision is the tracker set's — so this has to say what happened rather
    /// than answer with nothing.
    /// </remarks>
    [Fact]
    public async Task ATrackerThatTakesTheDatagramAndSaysNothingTimesOutAndNamesItself()
    {
        // Bound and never read from, so the datagram is taken and no answer
        // comes: a tracker that is up and not talking, which is not the same as
        // one that is not there.
        using UdpClient silent = new(new IPEndPoint(IPAddress.Loopback, 0));
        int port = ((IPEndPoint)silent.Client.LocalEndPoint!).Port;

        TimeoutException gave = await Assert.ThrowsAsync<TimeoutException>(
            () => new SocketTrackerTransport(new HttpClient()).ExchangeAsync(
                IPAddress.Loopback.ToString(),
                port,
                [1, 2, 3, 4],
                TimeSpan.FromMilliseconds(250),
                CancellationToken.None));

        Assert.Contains(port.ToString(), gave.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Nothing listening is a definite no, not a silence. The machine answers a
    /// datagram sent to a closed port with an ICMP refusal, and waiting out the
    /// full patience for an answer already given is fifteen seconds of a cycle
    /// spent on a tracker that is gone.
    /// </remarks>
    [Fact]
    public async Task ATrackerThatIsNotThereSaysSoAtOnceRatherThanWaiting()
    {
        int port;

        using (UdpClient nobody = new(new IPEndPoint(IPAddress.Loopback, 0)))
        {
            port = ((IPEndPoint)nobody.Client.LocalEndPoint!).Port;
        }

        TrackerException gone = await Assert.ThrowsAsync<TrackerException>(
            () => new SocketTrackerTransport(new HttpClient()).ExchangeAsync(
                IPAddress.Loopback.ToString(),
                port,
                [1, 2, 3, 4],
                TimeSpan.FromSeconds(15),
                CancellationToken.None));

        Assert.Contains(port.ToString(), gone.Message, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Most of the addresses a tracker hands out are stale. A peer that is not
    /// there is a peer that will not talk — the ordinary case — and one dead
    /// address must cost nothing above it.
    /// </remarks>
    [Fact]
    public async Task APeerThatIsNotThereAnswersNothingRatherThanThrowing()
    {
        int port;

        using (Socket nobody = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
        {
            nobody.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            port = ((IPEndPoint)nobody.LocalEndPoint!).Port;
        }

        Assert.Null(await new SocketPeerDialler(TimeSpan.FromSeconds(2)).DialAsync(
            new(IPAddress.Loopback, port),
            [.. Enumerable.Range(0, 20).Select(one => (byte)one)],
            [.. Enumerable.Range(0, 20).Select(one => (byte)one)],
            pieces: 8,
            CancellationToken.None));
    }
}
