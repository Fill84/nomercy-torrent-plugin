using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// The DHT over a real UDP socket.
/// </summary>
/// <remarks>
/// <para>
/// One socket for every question this client asks, because a node answers to
/// the address the question came from and a socket per question would need a
/// port per question. UDP carries no connection, so the only thing tying an
/// answer to its question is the transaction id inside it — which is why this
/// reads in one loop and hands each answer to whoever is waiting on that id,
/// rather than each caller reading whatever arrives next. Sending three
/// questions and reading three answers in order is how a client comes to
/// believe a node said something another node did.
/// </para>
/// <para>
/// An answer nobody is waiting for is dropped without a word: a node that
/// replies after the asker gave up is the ordinary case, and so is a stray
/// packet from anybody who cares to send one.
/// </para>
/// </remarks>
public sealed class SocketDhtTransport : IDhtTransport, IDisposable
{
    /// <summary>How long one question waits before it is given up on.</summary>
    /// <remarks>
    /// Five seconds, and a search asks many nodes at once — a dead node costs
    /// the search nothing but the wait, and most of a routing table is dead.
    /// </remarks>
    public static readonly TimeSpan DefaultPatience = TimeSpan.FromSeconds(5);

    /// <summary>The largest KRPC packet worth reading.</summary>
    /// <remarks>
    /// A get_peers answer with the maximum eight nodes and a token fits in a
    /// few hundred bytes; this is the ordinary datagram ceiling and leaves room
    /// for anything sane.
    /// </remarks>
    private const int MostBytes = 1500;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<KrpcMessage>> _waiting =
        new(StringComparer.Ordinal);

    private readonly Socket _socket;
    private readonly TimeSpan _patience;
    private readonly CancellationTokenSource _stopping = new();
    private bool _disposed;

    public SocketDhtTransport(TimeSpan? patience = null)
    {
        _patience = patience ?? DefaultPatience;

        _socket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));

        _ = ReadingAsync(_stopping.Token);
    }

    /// <summary>The port this client is asking from.</summary>
    public int Port => (_socket.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public async Task<KrpcMessage?> AskAsync(IPEndPoint node, byte[] query, CancellationToken ct)
    {
        if (_disposed)
        {
            return null;
        }

        string transaction = Transaction(query);

        if (transaction.Length == 0)
        {
            return null;
        }

        TaskCompletionSource<KrpcMessage> answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

        if (!_waiting.TryAdd(transaction, answer))
        {
            // Two questions under one id, which is the asker's mistake and not
            // this socket's to paper over.
            return null;
        }

        try
        {
            await _socket.SendToAsync(query, node, ct).ConfigureAwait(false);

            using CancellationTokenSource giveUp = CancellationTokenSource.CreateLinkedTokenSource(ct);

            giveUp.CancelAfter(_patience);

            return await answer.Task.WaitAsync(giveUp.Token).ConfigureAwait(false);
        }
        catch (Exception silent) when (silent is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // A node that is not there, a node that said nothing in time, a
            // network that refused the datagram. None of them is worth a line:
            // a search asks many and expects most to say nothing.
            return null;
        }
        finally
        {
            _waiting.TryRemove(transaction, out _);
        }
    }

    /// <summary>The one loop that reads, so an answer reaches the right asker.</summary>
    private async Task ReadingAsync(CancellationToken ct)
    {
        byte[] buffer = new byte[MostBytes];

        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult arrived;

            try
            {
                arrived = await _socket
                    .ReceiveFromAsync(buffer, new IPEndPoint(IPAddress.Any, 0), ct)
                    .ConfigureAwait(false);
            }
            catch (Exception gone)
            {
                if (gone is OperationCanceledException or ObjectDisposedException)
                {
                    return;
                }

                // A datagram that could not be read says nothing about the next
                // one. ICMP unreachable arrives here on Windows as a socket
                // error against a socket that is otherwise perfectly well.
                continue;
            }

            KrpcMessage message;

            try
            {
                message = Krpc.Read(buffer.AsSpan(0, arrived.ReceivedBytes));
            }
            catch (Exception)
            {
                // Anybody may send this port anything at all.
                continue;
            }

            if (_waiting.TryRemove(Convert.ToHexString(message.Transaction), out TaskCompletionSource<KrpcMessage>? asker))
            {
                asker.TrySetResult(message);
            }
        }
    }

    /// <summary>The id inside a question, as the answer will echo it.</summary>
    private static string Transaction(byte[] query)
    {
        try
        {
            return Convert.ToHexString(Krpc.Read(query).Transaction);
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _stopping.Cancel();
        _socket.Dispose();
        _stopping.Dispose();

        foreach (TaskCompletionSource<KrpcMessage> waiting in _waiting.Values)
        {
            waiting.TrySetCanceled();
        }

        _waiting.Clear();
    }
}
