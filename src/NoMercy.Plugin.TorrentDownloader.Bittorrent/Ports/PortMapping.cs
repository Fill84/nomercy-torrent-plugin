using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>Which way the port was opened, if it was.</summary>
public enum MappedBy
{
    /// <summary>Nothing worked. The client carries on regardless.</summary>
    Nothing,

    /// <summary>UPnP IGD, which is what most routers speak.</summary>
    Upnp,

    /// <summary>NAT-PMP, which is what most of the rest speak.</summary>
    NatPmp,
}

/// <summary>
/// What became of the attempt to have the router open a port.
/// </summary>
/// <param name="By">Which protocol managed it, or none.</param>
/// <param name="Port">The port that is open, when one is.</param>
/// <param name="Reason">
/// Why it did not work, in the router's own words where there are any. Kept
/// rather than thrown away: the Settings page says it and the owner forwards
/// the port by hand.
/// </param>
public sealed record PortMapResult(MappedBy By, int Port, string? Reason)
{
    /// <summary>Whether anything outside can reach us.</summary>
    public bool Mapped => By != MappedBy.Nothing;
}

/// <summary>One way of asking a router to open a port.</summary>
public interface IPortMapper
{
    /// <summary>What it is called, for the page and the log.</summary>
    string Name { get; }

    /// <summary>Asks. Answers what happened, and never throws for a router that says no.</summary>
    Task<PortMapResult> MapAsync(int port, CancellationToken ct);

    /// <summary>Closes it again on the way out.</summary>
    Task UnmapAsync(int port, CancellationToken ct);
}

/// <summary>
/// Getting the listening port opened, by whichever means the router has.
/// </summary>
/// <remarks>
/// <para>
/// UPnP first because most routers speak it, then NAT-PMP, from
/// docs/06-torrent-client.md. A router that answers neither is not an error:
/// the client still dials out and downloads perfectly well, it simply cannot be
/// dialled — so the failure is reported on the Settings page and everything
/// carries on.
/// </para>
/// <para>
/// The difference is not small in practice. Every peer this client has dialled
/// in a public swarm refused the connection, because they are behind their own
/// routers too; a mapped port is what lets any of them reach us.
/// </para>
/// </remarks>
public sealed class PortMapping(IReadOnlyList<IPortMapper> mappers)
{
    /// <summary>What happened last time it was tried.</summary>
    public PortMapResult Last { get; private set; } = new(MappedBy.Nothing, 0, null);

    /// <summary>Tries each in turn and stops at the first that works.</summary>
    public async Task<PortMapResult> MapAsync(int port, CancellationToken ct)
    {
        List<string> refusals = [];

        foreach (IPortMapper mapper in mappers)
        {
            PortMapResult result = await mapper.MapAsync(port, ct).ConfigureAwait(false);

            if (result.Mapped)
            {
                return Last = result;
            }

            refusals.Add($"{mapper.Name}: {result.Reason ?? "no answer"}");
        }

        // Every reason, not just the last: an owner reading this needs to know
        // whether the router said no or said nothing at all.
        return Last = new(MappedBy.Nothing, port, string.Join("; ", refusals));
    }

    /// <summary>Closes whatever was opened.</summary>
    public async Task UnmapAsync(int port, CancellationToken ct)
    {
        foreach (IPortMapper mapper in mappers)
        {
            await mapper.UnmapAsync(port, ct).ConfigureAwait(false);
        }

        Last = new(MappedBy.Nothing, port, null);
    }
}

/// <summary>
/// NAT-PMP, which is twelve bytes out and sixteen back.
/// </summary>
/// <remarks>
/// The gateway is asked directly on UDP 5351. It is the simpler of the two
/// protocols by a long way — no discovery, no XML, no SOAP — and it is the
/// fallback rather than the first choice only because fewer routers have it.
/// </remarks>
public static class NatPmp
{
    /// <summary>The port a gateway listens on.</summary>
    public const int GatewayPort = 5351;

    /// <summary>The version every message starts with.</summary>
    public const byte Version = 0;

    /// <summary>Asking about TCP.</summary>
    public const byte MapTcp = 2;

    /// <summary>
    /// How long a mapping lasts before the router forgets it.
    /// </summary>
    /// <remarks>
    /// Two hours. A mapping with no lifetime is one that survives this plugin
    /// being uninstalled, which leaves a stranger's port open on the owner's
    /// router for as long as it runs.
    /// </remarks>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromHours(2);

    /// <summary>Asks for a mapping.</summary>
    public static byte[] Write(int port, TimeSpan lifetime)
    {
        byte[] request = new byte[12];

        request[0] = Version;
        request[1] = MapTcp;

        // Bytes two and three are nought, which the specification reserves.
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)port);

        // What we would like it called outside: the same number, because a
        // client that asked for any free port would have to tell every tracker
        // a different one from the one it is listening on.
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(6), (ushort)port);
        BinaryPrimitives.WriteUInt32BigEndian(request.AsSpan(8), (uint)lifetime.TotalSeconds);

        return request;
    }

    /// <summary>Reads what the gateway said.</summary>
    public static PortMapResult Read(ReadOnlySpan<byte> answer, int wanted)
    {
        if (answer.Length < 16)
        {
            return new(MappedBy.Nothing, wanted, $"the gateway answered {answer.Length} bytes, and a mapping is sixteen");
        }

        // The op code comes back with the top bit set, which is how an answer
        // is told from a request arriving on the same socket.
        if (answer[1] != MapTcp + 128)
        {
            return new(MappedBy.Nothing, wanted, $"the gateway answered about something else (op {answer[1]})");
        }

        int code = BinaryPrimitives.ReadUInt16BigEndian(answer[2..]);

        return code == 0
            ? new(MappedBy.NatPmp, BinaryPrimitives.ReadUInt16BigEndian(answer[10..]), null)
            : new(MappedBy.Nothing, wanted, Refusal(code));
    }

    /// <summary>What a result code means, in words rather than a number.</summary>
    public static string Refusal(int code)
    {
        return code switch
        {
            1 => "the gateway speaks a newer version of NAT-PMP than this client",
            2 => "the gateway refuses to map ports, which is a setting on the router",
            3 => "the gateway has no network to map to",
            4 => "the gateway is out of resources",
            5 => "the gateway does not support this kind of mapping",
            _ => $"the gateway refused with code {code}",
        };
    }
}

/// <summary>
/// UPnP IGD: find the router, read what it can do, ask it.
/// </summary>
/// <remarks>
/// Three steps, each of which can fail on its own: a multicast search for
/// anything claiming to be a gateway, an HTTP GET of the description it points
/// at, and a SOAP call to the control address inside that description. The
/// messages are built and read here; the sockets belong to the shell.
/// </remarks>
public static class Upnp
{
    /// <summary>Where a search goes.</summary>
    public static IPAddress SearchGroup { get; } = IPAddress.Parse("239.255.255.250");

    /// <summary>And on which port.</summary>
    public const int SearchPort = 1900;

    /// <summary>The two kinds of gateway worth asking.</summary>
    public static IReadOnlyList<string> Gateways { get; } =
    [
        "urn:schemas-upnp-org:service:WANIPConnection:1",
        "urn:schemas-upnp-org:service:WANPPPConnection:1",
    ];

    /// <summary>The search packet.</summary>
    /// <remarks>
    /// <c>MX</c> is how long a device may wait before answering, so that a
    /// houseful of them does not answer at once. Three seconds, and a caller
    /// waits at least that long before deciding nothing is there.
    /// </remarks>
    public static byte[] Search(string target)
    {
        return Encoding.ASCII.GetBytes(
            "M-SEARCH * HTTP/1.1\r\n"
            + $"HOST: {SearchGroup}:{SearchPort}\r\n"
            + "MAN: \"ssdp:discover\"\r\n"
            + "MX: 3\r\n"
            + $"ST: {target}\r\n"
            + "\r\n");
    }

    /// <summary>The address of the description a search answer points at, or null.</summary>
    public static string? Location(ReadOnlySpan<byte> answer)
    {
        foreach (string line in Encoding.ASCII.GetString(answer).Split("\r\n"))
        {
            // Any case: routers spell this header every way there is.
            if (line.StartsWith("location:", StringComparison.OrdinalIgnoreCase))
            {
                return line["location:".Length..].Trim();
            }
        }

        return null;
    }

    /// <summary>
    /// The control address for the first gateway service in a description.
    /// </summary>
    /// <remarks>
    /// Found by looking for the service type and then the control address after
    /// it, rather than by insisting on the shape of the whole document: a real
    /// router's description carries a dozen services and several vendor
    /// extensions, and a strict reader would refuse a device that works.
    /// </remarks>
    public static string? ControlAddress(string description, out string? service)
    {
        foreach (string wanted in Gateways)
        {
            int at = description.IndexOf(wanted, StringComparison.OrdinalIgnoreCase);

            if (at < 0)
            {
                continue;
            }

            int open = description.IndexOf("<controlURL>", at, StringComparison.OrdinalIgnoreCase);
            int close = open < 0 ? -1 : description.IndexOf("</controlURL>", open, StringComparison.OrdinalIgnoreCase);

            if (open >= 0 && close > open)
            {
                service = wanted;

                return description[(open + "<controlURL>".Length)..close].Trim();
            }
        }

        service = null;

        return null;
    }

    /// <summary>The SOAP body that asks for a port.</summary>
    public static string AddPortMapping(string service, int port, IPAddress ours, TimeSpan lifetime)
    {
        return Envelope(
            service,
            "AddPortMapping",
            "<NewRemoteHost></NewRemoteHost>"
            + $"<NewExternalPort>{port}</NewExternalPort>"
            + "<NewProtocol>TCP</NewProtocol>"
            + $"<NewInternalPort>{port}</NewInternalPort>"
            + $"<NewInternalClient>{ours}</NewInternalClient>"
            + "<NewEnabled>1</NewEnabled>"

            // What the owner will see in the router's own list of mappings.
            // Recognisable on purpose: a mapping this plugin left behind should
            // be findable and removable by hand.
            + "<NewPortMappingDescription>NoMercy torrent</NewPortMappingDescription>"
            + $"<NewLeaseDuration>{(int)lifetime.TotalSeconds}</NewLeaseDuration>");
    }

    /// <summary>And the one that closes it again.</summary>
    public static string DeletePortMapping(string service, int port)
    {
        return Envelope(
            service,
            "DeletePortMapping",
            "<NewRemoteHost></NewRemoteHost>"
            + $"<NewExternalPort>{port}</NewExternalPort>"
            + "<NewProtocol>TCP</NewProtocol>");
    }

    /// <summary>What goes in the <c>SOAPAction</c> header, quotes included.</summary>
    public static string Action(string service, string action)
    {
        return $"\"{service}#{action}\"";
    }

    /// <summary>
    /// Whether a router's answer was a yes, and what it said if not.
    /// </summary>
    /// <remarks>
    /// A UPnP fault comes back as HTTP 500 with the reason inside the body, so
    /// the status alone says nothing useful. 725 in particular means the router
    /// will only do permanent mappings, and is worth saying in words because
    /// the answer to it is to ask again without a lease.
    /// </remarks>
    public static string? Refusal(string answer)
    {
        int code = answer.IndexOf("<errorCode>", StringComparison.OrdinalIgnoreCase);

        if (code < 0)
        {
            return null;
        }

        int close = answer.IndexOf("</errorCode>", code, StringComparison.OrdinalIgnoreCase);
        string number = close > code ? answer[(code + "<errorCode>".Length)..close].Trim() : "unknown";

        return int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed)
            ? parsed switch
            {
                402 => "the router did not understand the request (402)",
                501 => "the router tried and failed (501)",
                715 => "the router will not map for this address (715)",
                718 => "another device already has that port (718)",
                725 => "the router only does permanent mappings (725)",
                _ => $"the router refused with error {parsed}",
            }
            : $"the router refused with error {number}";
    }

    private static string Envelope(string service, string action, string body)
    {
        return "<?xml version=\"1.0\"?>"
               + "<s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" "
               + "s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\">"
               + "<s:Body>"
               + $"<u:{action} xmlns:u=\"{service}\">{body}</u:{action}>"
               + "</s:Body></s:Envelope>";
    }
}
