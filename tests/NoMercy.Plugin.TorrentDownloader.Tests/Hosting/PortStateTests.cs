using System.Net;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Which arrivals prove the listening port is open, and which prove nothing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only an address from outside is proof.</strong>
/// `docs/plan/DESIGN-2026-09-12-settings-and-pages.md` says "a peer has dialled
/// in from outside" and `S12-06` step 2 says the same, but the flag they both
/// rest on was set by any accepted socket at all. A peer on the owner's own
/// network — found by local service discovery, which this client announces to —
/// reaches the listening socket without crossing the router, so it says nothing
/// about whether the forwarded port works.
/// </para>
/// <para>
/// That matters because the page draws "open" from it, and a page that says a
/// shut port is open is the same fault as the notice this slice removed: a
/// sentence the owner cannot act on and cannot trust.
/// </para>
/// </remarks>
public class PortStateTests
{
    [Theory]
    [InlineData("203.0.113.7")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:db8::1")]
    public void ADialInFromOutsideProvesThePortIsOpen(string address)
    {
        Assert.True(DialIn.ProvesThePortIsOpen(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("127.0.0.1")]          // this machine
    [InlineData("10.0.0.4")]           // RFC 1918
    [InlineData("172.16.5.9")]         // RFC 1918
    [InlineData("172.31.255.254")]     // RFC 1918, the top of the range
    [InlineData("192.168.1.20")]       // RFC 1918, and the owner's own network
    [InlineData("100.64.0.1")]         // carrier-grade NAT, RFC 6598
    [InlineData("169.254.10.1")]       // link-local, no router involved
    [InlineData("::1")]                // this machine, over IPv6
    [InlineData("fe80::1")]            // link-local, over IPv6
    [InlineData("fd00::1")]            // unique-local, RFC 4193
    public void ADialInFromThisNetworkProvesNothing(string address)
    {
        Assert.False(DialIn.ProvesThePortIsOpen(IPAddress.Parse(address)));
    }

    /// <remarks>
    /// 172.15 and 172.32 are ordinary public addresses either side of the
    /// private block, and a range check written with the wrong bound refuses
    /// them. The block is 172.16 to 172.31 and nothing else.
    /// </remarks>
    [Theory]
    [InlineData("172.15.0.1")]
    [InlineData("172.32.0.1")]
    public void TheEdgesOfThePrivateBlockAreOutside(string address)
    {
        Assert.True(DialIn.ProvesThePortIsOpen(IPAddress.Parse(address)));
    }
}
