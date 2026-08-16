using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>What a client tells a tracker it is doing.</summary>
public enum AnnounceEvent
{
    /// <summary>Nothing in particular: the announce that happens on the interval.</summary>
    None,

    /// <summary>The first announce for this torrent in this session.</summary>
    Started,

    /// <summary>Everything is downloaded. Sent once, and never when it was already complete at the start.</summary>
    Completed,

    /// <summary>Leaving the swarm, politely.</summary>
    Stopped,
}

/// <summary>What a client tells a tracker about itself.</summary>
/// <param name="InfoHash">The torrent, as twenty raw bytes.</param>
/// <param name="PeerId">Twenty bytes identifying this client to the swarm.</param>
/// <param name="Port">Where peers can dial in.</param>
/// <param name="Uploaded">Bytes sent this session.</param>
/// <param name="Downloaded">Bytes received this session.</param>
/// <param name="Left">Bytes still wanted. Nought is what makes a peer a seed.</param>
/// <param name="Event">What has just happened, if anything.</param>
/// <param name="NumWant">How many peers are wanted back.</param>
public sealed record AnnounceRequest(
    byte[] InfoHash,
    byte[] PeerId,
    int Port,
    long Uploaded,
    long Downloaded,
    long Left,
    AnnounceEvent Event = AnnounceEvent.None,
    int NumWant = 50);

/// <summary>One peer a tracker named.</summary>
public sealed record PeerAddress(IPAddress Address, int Port)
{
    public override string ToString()
    {
        return $"{Address}:{Port}";
    }
}

/// <summary>
/// What a tracker answered.
/// </summary>
/// <param name="Interval">How long to wait before announcing again.</param>
/// <param name="MinInterval">The soonest it will tolerate being asked, when it says.</param>
/// <param name="Seeders">How many have all of it, or null when it does not say.</param>
/// <param name="Leechers">How many are still downloading, or null.</param>
/// <param name="Peers">Who to talk to.</param>
/// <param name="Failure">
/// What it refused with, in its own words. A tracker that says no answers with
/// this and nothing else, and a client that read the peer list first would see
/// an empty swarm rather than a reason.
/// </param>
public sealed record AnnounceResponse(
    TimeSpan Interval,
    TimeSpan? MinInterval,
    int? Seeders,
    int? Leechers,
    IReadOnlyList<PeerAddress> Peers,
    string? Failure = null)
{
    public bool Refused => Failure is not null;
}

/// <summary>
/// The HTTP tracker protocol: BEP 3, with compact peers from BEP 23.
/// </summary>
/// <remarks>
/// Only the bytes. What to do with them — when to announce, what to do when a
/// tracker will not answer — is the client's, and keeping the two apart is what
/// lets this be tested against a real tracker's real answer.
/// </remarks>
public static class HttpAnnounce
{
    /// <summary>
    /// The address to ask.
    /// </summary>
    /// <remarks>
    /// The info hash and the peer id are percent-encoded a byte at a time and
    /// never through a text encoder. They are bytes, not text: putting twenty
    /// raw bytes through UTF-8 turns every one above 0x7F into two, and the
    /// tracker then answers "not authorized" for a torrent it is serving —
    /// which is exactly what happened while this was being written.
    /// </remarks>
    public static Uri Address(string tracker, AnnounceRequest request)
    {
        StringBuilder query = new(tracker);

        query.Append(tracker.Contains('?', StringComparison.Ordinal) ? '&' : '?');
        query.Append("info_hash=").Append(Percent(request.InfoHash));
        query.Append("&peer_id=").Append(Percent(request.PeerId));
        query.Append("&port=").Append(request.Port);
        query.Append("&uploaded=").Append(request.Uploaded);
        query.Append("&downloaded=").Append(request.Downloaded);
        query.Append("&left=").Append(request.Left);
        query.Append("&compact=1");
        query.Append("&numwant=").Append(request.NumWant);

        if (request.Event != AnnounceEvent.None)
        {
            query.Append("&event=").Append(request.Event.ToString().ToLowerInvariant());
        }

        return new(query.ToString());
    }

    /// <summary>Reads what a tracker answered.</summary>
    /// <exception cref="BencodeFormatException">It answered something that is not bencode.</exception>
    public static AnnounceResponse Read(ReadOnlySpan<byte> answer)
    {
        if (Bencode.Read(answer).Root is not BencodeDictionary root)
        {
            throw new TrackerException("The tracker answered something that is not a dictionary.");
        }

        if (root.Text("failure reason") is string failure)
        {
            // A refusal and nothing else. Reading the peers first would show an
            // empty swarm where there is a reason.
            return new(TimeSpan.Zero, null, null, null, [], failure);
        }

        return new(
            TimeSpan.FromSeconds(root.Number("interval") ?? 1800),
            root.Number("min interval") is long minimum ? TimeSpan.FromSeconds(minimum) : null,
            (int?)root.Number("complete"),
            (int?)root.Number("incomplete"),
            PeersOf(root));
    }

    /// <summary>
    /// The peers, compact or otherwise.
    /// </summary>
    /// <remarks>
    /// Compact is six bytes each — four for the address and two big-endian for
    /// the port — and is what every tracker answers now. The older shape, a
    /// list of dictionaries, is still read because a tracker that has never
    /// been updated is exactly the one nobody will fix for us.
    /// </remarks>
    private static IReadOnlyList<PeerAddress> PeersOf(BencodeDictionary root)
    {
        List<PeerAddress> peers = [];

        switch (root["peers"])
        {
            case BencodeBytes compact:
                for (int at = 0; at + 6 <= compact.Value.Length; at += 6)
                {
                    peers.Add(new(
                        new IPAddress(compact.Value.AsSpan(at, 4)),
                        BinaryPrimitives.ReadUInt16BigEndian(compact.Value.AsSpan(at + 4, 2))));
                }

                break;

            case BencodeList listed:
                foreach (BencodeDictionary peer in listed.Items.OfType<BencodeDictionary>())
                {
                    if (peer.Text("ip") is string address
                        && peer.Number("port") is long port
                        && IPAddress.TryParse(address, out IPAddress? parsed))
                    {
                        peers.Add(new(parsed, (int)port));
                    }
                }

                break;

            default:
                break;
        }

        return peers;
    }

    private static string Percent(byte[] bytes)
    {
        StringBuilder text = new(bytes.Length * 3);

        foreach (byte value in bytes)
        {
            if (char.IsAsciiLetterOrDigit((char)value) || value is (byte)'-' or (byte)'_' or (byte)'.' or (byte)'~')
            {
                text.Append((char)value);
            }
            else
            {
                text.Append('%').Append(value.ToString("X2"));
            }
        }

        return text.ToString();
    }
}

/// <summary>A tracker that answered something a client cannot use.</summary>
public sealed class TrackerException(string message) : Exception(message);
