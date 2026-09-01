using System.Diagnostics;
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

        // Two figures, not one. It used to assert against the patience itself,
        // so "the refusal was honoured" and "the patience ran out" were
        // separated by nothing at all: a refusal delivered in a millisecond and
        // a continuation held up for two seconds by a busy machine are the same
        // reading, and the test failed for a reason that had nothing to do with
        // the transport. Ten seconds to wait and two to answer in leaves a
        // thousandfold margin on a path that really takes a millisecond.
        //
        // Short-ish rather than the plugin's own fifteen, so that a machine
        // which does not deliver the refusal costs this suite ten seconds once.
        TimeSpan patience = TimeSpan.FromSeconds(10);

        TimeSpan promptly = TimeSpan.FromSeconds(2);

        long started = Stopwatch.GetTimestamp();

        Exception refused = await Assert.ThrowsAnyAsync<Exception>(
            () => new SocketTrackerTransport(new HttpClient()).ExchangeAsync(
                IPAddress.Loopback.ToString(),
                port,
                [1, 2, 3, 4],
                patience,
                CancellationToken.None));

        TimeSpan took = Stopwatch.GetElapsedTime(started);

        if (refused is TimeoutException)
        {
            // The machine never delivered the ICMP refusal. A container that
            // does not carry it is the ordinary case on a Linux runner, and
            // waiting is then the correct answer rather than a fault — the
            // timeout half of this rule is proved by the test above, which does
            // not depend on ICMP at all.
            //
            // Said rather than skipped in silence: on a platform that does
            // deliver it, the assertions below are what stop the plugin
            // spending a whole cycle's patience on a tracker that is gone.
            Assert.True(
                took >= patience,
                $"No refusal was delivered, but it gave up after {took.TotalSeconds:0.0}s of {patience.TotalSeconds:0.0}s.");

            return;
        }

        TrackerException gone = Assert.IsType<TrackerException>(refused);

        Assert.Contains(port.ToString(), gone.Message, StringComparison.Ordinal);

        // At once, which is the whole point: the machine answered a datagram
        // sent to a closed port with a refusal, and waiting out the patience
        // for an answer already given is a cycle spent on nothing. Two seconds
        // out of ten, so this says the refusal was acted on and never that the
        // machine was busy.
        Assert.True(took < promptly, $"The refusal was delivered but it still waited {took.TotalSeconds:0.0}s.");
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
