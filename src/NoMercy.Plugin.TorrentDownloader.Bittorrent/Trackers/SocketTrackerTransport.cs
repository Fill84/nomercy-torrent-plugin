using System.Net.Sockets;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The wire under the trackers: a GET and a datagram.
/// </summary>
/// <remarks>
/// It decides nothing. Which tracker to ask, when, how long a connection id
/// lives and what to do when one will not answer are all <see cref="TrackerSet"/>'s
/// and are tested without a socket; this is the part that has nothing to decide
/// and cannot be judged without a network.
/// </remarks>
public sealed class SocketTrackerTransport(HttpClient http) : ITrackerTransport
{
    public async Task<byte[]> GetAsync(Uri address, CancellationToken ct)
    {
        return await http.GetByteArrayAsync(address, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Sends one datagram and waits for one back.
    /// </summary>
    /// <remarks>
    /// A socket per exchange rather than one kept open. A tracker's answer
    /// arrives on the port the request went out from, and a shared socket would
    /// have two announces racing to read each other's replies — the transaction
    /// id would catch it, but only by throwing away an answer that had arrived.
    /// </remarks>
    public async Task<byte[]> ExchangeAsync(
        string host,
        int port,
        byte[] datagram,
        TimeSpan patience,
        CancellationToken ct)
    {
        using UdpClient socket = new(AddressFamily.InterNetwork);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

        waiting.CancelAfter(patience);

        await socket.SendAsync(datagram, host, port, waiting.Token).ConfigureAwait(false);

        try
        {
            UdpReceiveResult answer = await socket.ReceiveAsync(waiting.Token).ConfigureAwait(false);

            return answer.Buffer;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Ours ran out, not the caller's. BEP 15 says to try again with a
            // longer wait, and that decision belongs to the tracker set — which
            // is why this says what happened rather than swallowing it.
            throw new TimeoutException($"{host}:{port} did not answer within {patience.TotalSeconds:0.#} seconds.");
        }
        catch (SocketException refused)
        {
            // Nothing is listening there. The machine answers a datagram sent
            // to a closed port with an ICMP refusal, which arrives here as a
            // reset on the next read — so this is a definite no rather than a
            // silence, and waiting out the patience for it would be fifteen
            // seconds spent on an answer already given.
            throw new TrackerException($"{host}:{port} is not listening: {refused.SocketErrorCode}.");
        }
    }
}
