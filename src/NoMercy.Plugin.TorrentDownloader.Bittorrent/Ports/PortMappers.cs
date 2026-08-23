using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace NoMercy.Plugin.TorrentDownloader.Bittorrent;

/// <summary>
/// Which router this machine talks to, and under what address.
/// </summary>
/// <remarks>
/// Neither protocol can be attempted without this: NAT-PMP asks the gateway
/// directly, and UPnP has to tell the router which machine on the inside the
/// port belongs to. Both answers come from the same adapter, so they are found
/// together — the gateway of one adapter with the local address of another maps
/// a port to a machine that is not this one.
/// </remarks>
public sealed record Router(IPAddress Gateway, IPAddress Ours)
{
    /// <summary>The first adapter that is up and has a gateway, or null.</summary>
    /// <remarks>
    /// IPv4 only. A mapping is for peers dialling in from the outside, and a
    /// machine with a routable IPv6 address does not need one.
    /// </remarks>
    public static Router? Find()
    {
        foreach (NetworkInterface adapter in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (adapter.OperationalStatus != OperationalStatus.Up
                || adapter.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            IPInterfaceProperties properties = adapter.GetIPProperties();

            IPAddress? gateway = properties.GatewayAddresses
                .Select(one => one.Address)
                .FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork && !one.Equals(IPAddress.Any));

            if (gateway is null)
            {
                continue;
            }

            IPAddress? ours = properties.UnicastAddresses
                .Select(one => one.Address)
                .FirstOrDefault(one => one.AddressFamily == AddressFamily.InterNetwork);

            if (ours is not null)
            {
                return new(gateway, ours);
            }
        }

        return null;
    }
}

/// <summary>
/// NAT-PMP over a real socket.
/// </summary>
/// <remarks>
/// The messages are <see cref="NatPmp"/>'s and were written and tested in
/// Sprint 6; this is the socket they were written for, which never existed —
/// so the setting said the port would be mapped and no port was ever mapped.
/// </remarks>
public sealed class NatPmpMapper(Func<Router?> router, TimeSpan patience) : IPortMapper
{
    public NatPmpMapper()
        : this(Router.Find, TimeSpan.FromSeconds(3))
    {
    }

    public string Name => "NAT-PMP";

    public Task<PortMapResult> MapAsync(int port, CancellationToken ct)
    {
        return AskAsync(port, NatPmp.Lifetime, ct);
    }

    public async Task UnmapAsync(int port, CancellationToken ct)
    {
        // A lifetime of nought is how the protocol spells "take it away".
        await AskAsync(port, TimeSpan.Zero, ct).ConfigureAwait(false);
    }

    private async Task<PortMapResult> AskAsync(int port, TimeSpan lifetime, CancellationToken ct)
    {
        if (router() is not Router found)
        {
            return new(MappedBy.Nothing, port, "this machine has no default gateway on any network it is on");
        }

        try
        {
            using UdpClient socket = new();
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

            waiting.CancelAfter(patience);

            await socket
                .SendAsync(NatPmp.Write(port, lifetime), new IPEndPoint(found.Gateway, NatPmp.GatewayPort), waiting.Token)
                .ConfigureAwait(false);

            UdpReceiveResult answer = await socket.ReceiveAsync(waiting.Token).ConfigureAwait(false);

            return NatPmp.Read(answer.Buffer, port);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // The ordinary case on a router that has never heard of NAT-PMP:
            // the datagram goes nowhere and nothing comes back at all.
            return new(MappedBy.Nothing, port, $"{found.Gateway} did not answer within {patience.TotalSeconds:0.#} seconds");
        }
        catch (SocketException refused)
        {
            return new(MappedBy.Nothing, port, refused.Message);
        }
    }
}

/// <summary>
/// UPnP IGD over real sockets: search, describe, ask.
/// </summary>
/// <remarks>
/// <para>
/// Three steps and any of them can fail on its own — a multicast search for
/// anything claiming to be a gateway, an HTTP GET of the description it points
/// at, and a SOAP call to the control address inside it. Every message here is
/// built and read by <see cref="Upnp"/>, which was written and tested in
/// Sprint 6 with nothing to call it.
/// </para>
/// <para>
/// A router that says no is not a fault and never throws: the client still
/// dials out and downloads, it simply cannot be dialled, and the reason is put
/// on the Settings page so the owner can forward the port by hand.
/// </para>
/// </remarks>
public sealed class UpnpMapper(Func<Router?> router, HttpClient http, TimeSpan patience) : IPortMapper
{
    public UpnpMapper(HttpClient http)
        : this(Router.Find, http, TimeSpan.FromSeconds(3))
    {
    }

    public string Name => "UPnP";

    public async Task<PortMapResult> MapAsync(int port, CancellationToken ct)
    {
        if (router() is not Router found)
        {
            return new(MappedBy.Nothing, port, "this machine has no default gateway on any network it is on");
        }

        (string? control, string? service, string? why) = await ControlAsync(ct).ConfigureAwait(false);

        if (control is null || service is null)
        {
            return new(MappedBy.Nothing, port, why);
        }

        string? refused = await CallAsync(
                control,
                service,
                "AddPortMapping",
                Upnp.AddPortMapping(service, port, found.Ours, Upnp.Lifetime),
                ct)
            .ConfigureAwait(false);

        if (refused is null)
        {
            return new(MappedBy.Upnp, port, null);
        }

        // 725 is the router saying it will only do permanent mappings, and the
        // answer to it is to ask again without a lease rather than to give up.
        if (!refused.Contains("725", StringComparison.Ordinal))
        {
            return new(MappedBy.Nothing, port, refused);
        }

        string? again = await CallAsync(
                control,
                service,
                "AddPortMapping",
                Upnp.AddPortMapping(service, port, found.Ours, TimeSpan.Zero),
                ct)
            .ConfigureAwait(false);

        return again is null ? new(MappedBy.Upnp, port, null) : new(MappedBy.Nothing, port, again);
    }

    public async Task UnmapAsync(int port, CancellationToken ct)
    {
        (string? control, string? service, string? _) = await ControlAsync(ct).ConfigureAwait(false);

        if (control is null || service is null)
        {
            return;
        }

        await CallAsync(control, service, "DeletePortMapping", Upnp.DeletePortMapping(service, port), ct)
            .ConfigureAwait(false);
    }

    /// <summary>The router's control address, found by searching and then reading.</summary>
    private async Task<(string? Control, string? Service, string? Why)> ControlAsync(CancellationToken ct)
    {
        string? location = await SearchAsync(ct).ConfigureAwait(false);

        if (location is null)
        {
            return (null, null, "no device on this network answered a UPnP search");
        }

        try
        {
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

            waiting.CancelAfter(patience);

            string description = await http.GetStringAsync(location, waiting.Token).ConfigureAwait(false);
            string? control = Upnp.ControlAddress(description, out string? service);

            if (control is null || service is null)
            {
                return (null, null, $"the device at {location} describes no gateway service");
            }

            // Relative in most descriptions, and absolute in some.
            return (new Uri(new Uri(location), control).ToString(), service, null);
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or UriFormatException
                                                or OperationCanceledException
                                            && !ct.IsCancellationRequested)
        {
            return (null, null, $"the device at {location} could not be read: {unreachable.Message}");
        }
    }

    /// <summary>Multicasts a search and answers the first location offered.</summary>
    private async Task<string?> SearchAsync(CancellationToken ct)
    {
        try
        {
            using UdpClient socket = new(new IPEndPoint(IPAddress.Any, 0));
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

            waiting.CancelAfter(patience);

            IPEndPoint group = new(Upnp.SearchGroup, Upnp.SearchPort);

            foreach (string target in Upnp.Gateways)
            {
                await socket.SendAsync(Upnp.Search(target), group, waiting.Token).ConfigureAwait(false);
            }

            while (!waiting.IsCancellationRequested)
            {
                UdpReceiveResult answer = await socket.ReceiveAsync(waiting.Token).ConfigureAwait(false);

                if (Upnp.Location(answer.Buffer) is string location)
                {
                    return location;
                }

                // Something else on the network answered the multicast. Read
                // on: a household has printers and televisions that all reply.
            }

            return null;
        }
        catch (Exception quiet) when (quiet is OperationCanceledException or SocketException
                                      && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>One SOAP call. Answers the refusal, or null when it worked.</summary>
    private async Task<string?> CallAsync(
        string control,
        string service,
        string action,
        string body,
        CancellationToken ct)
    {
        try
        {
            using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(ct);

            waiting.CancelAfter(patience);

            using HttpRequestMessage asking = new(HttpMethod.Post, control)
            {
                Content = new StringContent(body, Encoding.UTF8, "text/xml"),
            };

            asking.Headers.TryAddWithoutValidation("SOAPAction", Upnp.Action(service, action));

            using HttpResponseMessage answer = await http.SendAsync(asking, waiting.Token).ConfigureAwait(false);

            string said = await answer.Content.ReadAsStringAsync(waiting.Token).ConfigureAwait(false);

            // A UPnP fault is an HTTP 500 with the reason inside it, so the
            // status alone says nothing useful and the body is what is read.
            return Upnp.Refusal(said) ?? (answer.IsSuccessStatusCode ? null : $"the router answered {(int)answer.StatusCode}");
        }
        catch (Exception unreachable) when (unreachable is HttpRequestException or OperationCanceledException
                                            && !ct.IsCancellationRequested)
        {
            return unreachable.Message;
        }
    }
}
