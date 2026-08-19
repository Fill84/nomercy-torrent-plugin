using System.Security.Cryptography;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// What this client calls itself on the wire.
/// </summary>
/// <remarks>
/// Twenty bytes, by BEP 3, and opaque to the protocol. The shape is the Azureus
/// convention — a dash, two letters for the client, four digits of version, a
/// dash, then twelve random bytes — because every real client follows it so
/// that trackers and peers can tell software apart. The specifications name no
/// format for this plugin, so the convention is applied rather than a rule
/// invented.
/// </remarks>
public static class PeerIdentity
{
    /// <summary>
    /// The client and version this build announces itself as.
    /// </summary>
    /// <remarks>
    /// NM for NoMercy, 0400 for 0.4.0. It goes out to every tracker and every
    /// peer, which is the reason it says a version at all: a swarm refusing
    /// this client is a fact somebody has to be able to connect to a release.
    /// </remarks>
    public const string Client = "-NM0400-";

    /// <summary>A peer id nobody else is using.</summary>
    /// <remarks>
    /// Random after the name, and not derived from anything. Two servers
    /// sharing a peer id is two clients a tracker counts as one and a peer
    /// refuses as itself, and it would not be noticed until two of them ran on
    /// one network.
    /// </remarks>
    public static byte[] New()
    {
        byte[] id = new byte[20];

        Encoding.ASCII.GetBytes(Client).CopyTo(id, 0);
        RandomNumberGenerator.Fill(id.AsSpan(Client.Length));

        return id;
    }
}
