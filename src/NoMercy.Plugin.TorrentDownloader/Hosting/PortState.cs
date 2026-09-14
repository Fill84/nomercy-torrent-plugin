using System.Net;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// What is known about the listening port, which is less than it looks.
/// </summary>
/// <remarks>
/// <para>
/// Three states, decided from one fact: whether anything outside has got
/// through. <strong>A failed UPnP or NAT-PMP attempt is not one of them.</strong>
/// It says the router would not open the port <em>by itself</em> — and on the
/// owner's network neither protocol has ever answered while 51413 has been
/// forwarded by hand for months, so the page's one notice was the one thing on
/// it that was wrong. A notice that is always wrong is how an owner learns to
/// read past every notice.
/// </para>
/// <para>
/// So the router's answer is logged and never drawn, and the port stays
/// <see cref="Unknown"/> until something proves otherwise.
/// </para>
/// </remarks>
public enum PortState
{
    /// <summary>
    /// Nothing has proved it either way, which is the honest answer on an idle
    /// server and the one it will usually give.
    /// </summary>
    Unknown,

    /// <summary>
    /// Something outside got through. The only proof there is that the port is
    /// open, because nothing can reach a port that is shut.
    /// </summary>
    Open,

    /// <summary>
    /// A live check asked and the port did not answer.
    /// </summary>
    /// <remarks>
    /// Built and set by nobody yet. <c>INetworkDiscovery.IsPortOpenAsync()</c>
    /// exists on the host but is wired to the server's own external web port;
    /// media-server #52 asks for an overload taking a port, and #53 for the
    /// seam a plugin reaches it through. Until #52 lands there is no way to
    /// learn this, and inventing it from a mapping refusal is exactly the
    /// wrong answer this type exists to stop.
    /// </remarks>
    Shut,
}

/// <summary>
/// Whether a peer's arrival proves anything about the listening port.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only an arrival from outside proves it.</strong> The design and
/// <c>S12-06</c> both say "a peer has dialled in from outside", but the flag
/// they rest on was set by any accepted socket at all — and a peer on the
/// owner's own network, found by the local service discovery this client
/// announces to, reaches the socket without ever crossing the router. It says
/// nothing about whether the forwarded port works.
/// </para>
/// <para>
/// Drawing "open" from such an arrival would be the same fault as the notice
/// this slice removed, in the other direction: a page confidently wrong about
/// the one thing the owner would act on.
/// </para>
/// </remarks>
public static class DialIn
{
    /// <summary>Whether <paramref name="address"/> can only have come through the router.</summary>
    public static bool ProvesThePortIsOpen(IPAddress address)
    {
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal)
        {
            return false;
        }

        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
        {
            // Anything else over IPv6 crossed something to get here.
            return true;
        }

        byte[] octets = address.GetAddressBytes();

        return octets[0] switch
        {
            // RFC 1918.
            10 => false,

            // RFC 1918, 172.16 to 172.31 — and no wider, or two public ranges
            // either side of it are refused with it.
            172 => octets[1] is < 16 or > 31,

            // RFC 1918, and RFC 3927 link-local: no router was involved.
            192 => octets[1] != 168,
            169 => octets[1] != 254,

            // RFC 6598, carrier-grade NAT: 100.64 to 100.127.
            100 => octets[1] is < 64 or > 127,
            _ => true,
        };
    }
}
