using System.Net;
using System.Net.Sockets;
using System.Text;

using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// The DHT's socket, against a node on this machine.
/// </summary>
/// <remarks>
/// UDP carries no connection, so the only thing tying an answer to its question
/// is the transaction id inside it. A transport that handed each caller whatever
/// datagram arrived next would let one node's answer be read as another's — and
/// a search asks many nodes at once, so that is the ordinary case rather than a
/// rare one.
/// </remarks>
public class SocketDhtTransportTests
{
    [Fact]
    public async Task AnAnswerGoesToWhoeverAskedThatQuestion()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));
        using Socket node = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        node.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        IPEndPoint at = (IPEndPoint)node.LocalEndPoint!;

        // A node that answers slowly first and quickly second, so the two
        // answers come back in the opposite order to the questions. Read in
        // arrival order, the first asker would be handed the second's answer.
        Task pretending = Task.Run(
            async () =>
            {
                byte[] buffer = new byte[1500];

                for (int answered = 0; answered < 2; answered++)
                {
                    SocketReceiveFromResult came = await node.ReceiveFromAsync(
                        buffer,
                        new IPEndPoint(IPAddress.Any, 0),
                        stopping.Token);

                    KrpcMessage asked = Krpc.Read(buffer.AsSpan(0, came.ReceivedBytes));

                    if (answered == 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(700), stopping.Token);
                    }

                    await node.SendToAsync(Answer(asked.Transaction), came.RemoteEndPoint, stopping.Token);
                }
            },
            stopping.Token);

        using SocketDhtTransport transport = new();

        NodeId ours = NodeId.Random();

        byte[] first = Krpc.WritePing("aa"u8, ours);
        byte[] second = Krpc.WritePing("bb"u8, ours);

        Task<KrpcMessage?> slow = transport.AskAsync(at, first, stopping.Token);
        Task<KrpcMessage?> quick = transport.AskAsync(at, second, stopping.Token);

        KrpcMessage?[] both = await Task.WhenAll(slow, quick);

        await pretending;

        Assert.NotNull(both[0]);
        Assert.NotNull(both[1]);

        Assert.Equal("aa", Encoding.ASCII.GetString(both[0]!.Transaction));
        Assert.Equal("bb", Encoding.ASCII.GetString(both[1]!.Transaction));
    }

    /// <remarks>
    /// Most of a routing table is dead, so this is the ordinary answer rather
    /// than a fault: a node that says nothing costs the search the wait and no
    /// more.
    /// </remarks>
    [Fact]
    public async Task ANodeThatSaysNothingIsAnsweredWithNothing()
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(20));
        using Socket silent = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);

        silent.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        using SocketDhtTransport transport = new(TimeSpan.FromMilliseconds(300));

        Assert.Null(await transport.AskAsync(
            (IPEndPoint)silent.LocalEndPoint!,
            Krpc.WritePing("cc"u8, NodeId.Random()),
            stopping.Token));
    }

    /// <summary>A reply that names itself and echoes the question's id.</summary>
    private static byte[] Answer(byte[] transaction)
    {
        byte[] id = NodeId.Random().Bytes.ToArray();

        return
        [
            .. "d1:rd2:id20:"u8.ToArray(),
            .. id,
            .. "e1:t"u8.ToArray(),
            .. Encoding.ASCII.GetBytes(transaction.Length.ToString()),
            .. ":"u8.ToArray(),
            .. transaction,
            .. "1:y1:re"u8.ToArray(),
        ];
    }
}
