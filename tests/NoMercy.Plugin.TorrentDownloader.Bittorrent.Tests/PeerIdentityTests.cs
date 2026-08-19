using System.Text;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests;

/// <summary>
/// What this client calls itself on the wire.
/// </summary>
/// <remarks>
/// Twenty bytes, by BEP 3, and opaque to the protocol — but every real client
/// follows the Azureus convention so that trackers and peers can tell software
/// apart, and one that did not would be the odd one out in every swarm it
/// joined. The specs name no format, so this is the convention applied rather
/// than a rule invented.
/// </remarks>
public class PeerIdentityTests
{
    [Fact]
    public void ItIsTwentyBytesAndNamesThisClientAndItsVersion()
    {
        byte[] id = PeerIdentity.New();

        Assert.Equal(20, id.Length);
        Assert.Equal("-NM0400-", Encoding.ASCII.GetString(id, 0, 8));
    }

    /// <remarks>
    /// Two clients sharing a peer id is two clients a tracker counts as one and
    /// a peer refuses as itself. The twelve bytes after the name are random for
    /// exactly that reason, and a constant there would be the one fault nobody
    /// would see until two servers ran on one network.
    /// </remarks>
    [Fact]
    public void TwoAreNotTheSame()
    {
        Assert.NotEqual(PeerIdentity.New(), PeerIdentity.New());
    }
}
