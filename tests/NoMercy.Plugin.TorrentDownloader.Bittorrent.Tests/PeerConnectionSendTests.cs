using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// Several parts of the client writing to one peer at the same moment.
/// </summary>
/// <remarks>
/// <para>
/// A connection is written by more than its own conversation: the keep-alive
/// beat, the read loop answering a request, and every other peer's conversation
/// telling this one about a piece that just verified. None of them waits for
/// the others, so sends overlap in production as a matter of course.
/// </para>
/// <para>
/// Both ends are this client, over a real loopback socket, after a real MSE
/// negotiation. What is asserted is what the far end reads: every block, whole,
/// in frames it can make sense of.
/// </para>
/// </remarks>
public class PeerConnectionSendTests
{
    private const int Senders = 8;

    private const int BlocksEach = 50;

    /// <remarks>
    /// <para>
    /// Encrypted, the RC4 keystream is one running state per direction. Two
    /// sends that encrypt at the same moment tear that state between them, and
    /// two that encrypt in one order and reach the socket in the other hand
    /// the peer bytes out of keystream order. Either way every byte after the
    /// fault decrypts to rubbish on the far side, and the peer drops the
    /// connection on a message length that makes no sense.
    /// </para>
    /// <para>
    /// In the clear the frames survived on this machine's loopback, but the
    /// upload counter did not: every overlapping send read it and wrote it back
    /// with only its own block added, and the rate meter reads that counter.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData(MseMethod.Rc4)]
    [InlineData(MseMethod.Plaintext)]
    public async Task BlocksSentFromManyTasksAtOnceAllArriveWhole(MseMethod method)
    {
        using CancellationTokenSource stopping = new(TimeSpan.FromSeconds(30));

        (PeerConnection sending, PeerConnection receiving) = await ConnectedAsync(method, stopping.Token);

        using PeerConnection sender = sending;
        using PeerConnection receiver = receiving;

        Task[] sends = new Task[Senders];

        for (int task = 0; task < Senders; task++)
        {
            int first = task * BlocksEach;

            sends[task] = Task.Run(async () =>
            {
                for (int piece = first; piece < first + BlocksEach; piece++)
                {
                    await sender.SendAsync(PeerMessage.Block(piece, 0, Pattern(piece)), stopping.Token);
                }
            }, stopping.Token);
        }

        HashSet<int> arrived = [];
        string? fault = null;

        try
        {
            while (arrived.Count < Senders * BlocksEach)
            {
                PeerMessage? message = await receiver.NextAsync(stopping.Token);

                if (message is null)
                {
                    fault = "the connection closed";
                    break;
                }

                if (message.Id != PeerMessageId.Piece)
                {
                    fault = $"a {message.Id?.ToString() ?? "keep-alive"} arrived where only blocks were sent";
                    break;
                }

                (int piece, int offset, byte[] data) = message.AsBlock();

                if (offset != 0 || !data.AsSpan().SequenceEqual(Pattern(piece)) || !arrived.Add(piece))
                {
                    fault = $"block {piece} at {offset} arrived damaged or twice";
                    break;
                }
            }
        }
        catch (Exception unreadable) when (unreadable is PeerProtocolException or OperationCanceledException)
        {
            fault = $"the stream stopped making sense after {arrived.Count} blocks: {unreadable.Message}";
        }

        Assert.Null(fault);
        Assert.Equal(Senders * BlocksEach, arrived.Count);

        await Task.WhenAll(sends);

        // The counter the rate meter reads, bumped by every send at once.
        Assert.Equal((long)Senders * BlocksEach * PeerMessage.BlockLength, sender.Uploaded);
    }

    /// <summary>Sixteen kibibytes that say which block they are, so a torn one shows.</summary>
    private static byte[] Pattern(int piece)
    {
        byte[] data = new byte[PeerMessage.BlockLength];

        for (int at = 0; at < data.Length; at++)
        {
            data[at] = (byte)((piece * 31) + at);
        }

        return data;
    }

    /// <summary>Two ends of one loopback socket, negotiated to the method asked for.</summary>
    private static async Task<(PeerConnection Sending, PeerConnection Receiving)> ConnectedAsync(
        MseMethod method,
        CancellationToken ct)
    {
        TcpListener listener = new(IPAddress.Loopback, 0);

        listener.Start();

        TcpClient dialling = new();
        TcpClient accepted;

        try
        {
            Task connecting = dialling.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port, ct).AsTask();

            accepted = await listener.AcceptTcpClientAsync(ct);

            await connecting;
        }
        finally
        {
            listener.Stop();
        }

        byte[] ours = Handshake.Write(Ubuntu, PeerId("SEND"));
        byte[] theirs = Handshake.Write(Ubuntu, PeerId("RECV"));

        Task<MseLink> initiating = MseNegotiation.InitiateAsync(
            dialling.GetStream(), Ubuntu, ours, MseMethod.Plaintext | MseMethod.Rc4, RandomNumberGenerator.Create(), ct);

        Task<MseLink> accepting = MseNegotiation.AcceptAsync(
            accepted.GetStream(), [Ubuntu], method, RandomNumberGenerator.Create(), ct);

        MseLink[] both = await Task.WhenAll(initiating, accepting);

        Assert.Equal(method, both[0].Method);

        PeerConnection sending = new(both[0].Stream, Handshake.Read(theirs)!, 0);
        PeerConnection receiving = new(both[1].Stream, Handshake.Read(both[1].Initial)!, 0);

        return (sending, receiving);
    }

    /// <summary>Ubuntu's published info hash; any real one would do.</summary>
    private static byte[] Ubuntu =>
        Convert.FromHexString("D160B8D8EA35A5B4E52837468FC8F03D55CEF1F7");

    private static byte[] PeerId(string name)
    {
        return [.. "-NM0400-"u8, .. System.Text.Encoding.ASCII.GetBytes(name.PadRight(12, '0'))];
    }
}
