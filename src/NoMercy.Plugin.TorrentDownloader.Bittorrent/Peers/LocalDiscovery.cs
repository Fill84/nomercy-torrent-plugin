using System.Net;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Somebody on this network saying what they have.
/// </summary>
/// <param name="Port">The port they listen on.</param>
/// <param name="InfoHashes">Which torrents, upper-case hex.</param>
/// <param name="Cookie">
/// What that client calls itself, so it can recognise and ignore its own
/// announces coming back round the multicast group.
/// </param>
public sealed record LsdAnnounce(int Port, IReadOnlyList<string> InfoHashes, string? Cookie);

/// <summary>
/// BEP 14: finding peers on the same network without asking anybody outside it.
/// </summary>
/// <remarks>
/// <para>
/// A server and a desktop in the same house downloading the same thing should
/// not be sending it to each other through a tracker's idea of the internet.
/// One multicast packet finds them, and the transfer runs at the speed of the
/// switch between them.
/// </para>
/// <para>
/// The message is HTTP-shaped and is not HTTP: nothing answers it, and the
/// headers are read by position of name rather than parsed by a web server.
/// </para>
/// </remarks>
public static class LocalDiscovery
{
    /// <summary>The group every client on the network listens to.</summary>
    public static IPAddress Group { get; } = IPAddress.Parse("239.192.152.143");

    /// <summary>The port it is on.</summary>
    public const int GroupPort = 6771;

    /// <summary>
    /// How often the same torrent is announced.
    /// </summary>
    /// <remarks>
    /// BEP 14 says a client must not announce more often than every five
    /// minutes. It is a local network and nobody is paying for the packet, but
    /// a client that shouted every second would be the reason somebody turns
    /// this off.
    /// </remarks>
    public static TimeSpan Interval { get; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Writes an announce.
    /// </summary>
    /// <remarks>
    /// The line endings are the message. Every header ends in a carriage return
    /// and a newline and the whole thing ends in two more — a client reading it
    /// as lines of text with the wrong ending sees one header where there are
    /// four.
    /// </remarks>
    public static byte[] Write(int port, IEnumerable<string> infoHashes, string cookie)
    {
        StringBuilder message = new();

        message.Append("BT-SEARCH * HTTP/1.1\r\n");
        message.Append($"Host: {Group}:{GroupPort}\r\n");
        message.Append($"Port: {port}\r\n");

        foreach (string hash in infoHashes)
        {
            // One line each, which is how BEP 14 says a client announces
            // several torrents in one packet.
            message.Append($"Infohash: {hash.ToUpperInvariant()}\r\n");
        }

        message.Append($"cookie: {cookie}\r\n");
        message.Append("\r\n\r\n");

        return Encoding.ASCII.GetBytes(message.ToString());
    }

    /// <summary>
    /// Reads one, or null when it is not an announce or is our own.
    /// </summary>
    /// <param name="packet">What arrived on the group.</param>
    /// <param name="ours">
    /// This client's own cookie. Every packet it sends comes straight back to
    /// it, and a client that took its own announce would connect to itself.
    /// </param>
    public static LsdAnnounce? Read(ReadOnlySpan<byte> packet, string ours)
    {
        string[] lines = Encoding.ASCII.GetString(packet).Split("\r\n");

        if (lines.Length == 0 || !lines[0].StartsWith("BT-SEARCH ", StringComparison.Ordinal))
        {
            // Something else on the group, of which there is plenty.
            return null;
        }

        int port = 0;
        List<string> hashes = [];
        string? cookie = null;

        foreach (string line in lines.Skip(1))
        {
            string[] parts = line.Split(':', 2);

            if (parts.Length != 2)
            {
                continue;
            }

            // The names are matched without regard to case: BEP 14 spells
            // "cookie" in lower case and "Infohash" in mixed, and real clients
            // do as they please.
            switch (parts[0].Trim().ToLowerInvariant())
            {
                case "port":
                    _ = int.TryParse(parts[1].Trim(), out port);
                    break;

                case "infohash":
                    string hash = parts[1].Trim().ToUpperInvariant();

                    if (hash.Length == 40 && hash.All(Uri.IsHexDigit))
                    {
                        hashes.Add(hash);
                    }

                    break;

                case "cookie":
                    cookie = parts[1].Trim();
                    break;

                default:
                    break;
            }
        }

        if (cookie is not null && string.Equals(cookie, ours, StringComparison.Ordinal))
        {
            // Ourselves, come back round the group.
            return null;
        }

        return port is > 0 and <= 65535 && hashes.Count > 0 ? new(port, hashes, cookie) : null;
    }

    /// <summary>
    /// What to announce, out of the torrents this client is holding.
    /// </summary>
    /// <remarks>
    /// A private torrent is not among them, and that is the whole of BEP 27's
    /// rule here: the packet carries the info hash in the clear to everybody on
    /// the network, which is precisely what a private tracker's members are
    /// forbidden to do.
    /// </remarks>
    public static IReadOnlyList<string> Announceable(IEnumerable<TorrentMetadata> torrents)
    {
        return [.. torrents.Where(one => !one.Private).Select(one => one.InfoHash)];
    }
}
