// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Buffers.Binary;
using System.Net;
using System.Text;
using NoMercy.Plugin.TorrentDownloader.Core.Bencode;

namespace NoMercy.Plugin.TorrentDownloader.Core.Trackers;

public sealed class HttpTracker(HttpClient client) : IPeerSource
{
    private const int CompactEntryLength = 6;

    /// <summary>Used when a tracker names no interval. Frequent enough to keep the swarm fresh, rare enough not to be rude.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public bool CanAnnounceTo(string url) =>
        url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public async Task<AnnounceResult> AnnounceAsync(string url, AnnounceRequest request, CancellationToken ct)
    {
        HttpResponseMessage response;

        // Canonicalisation off. Uri otherwise "helpfully" unescapes sequences it thinks
        // are safe - %20 becomes a literal space - and the info hash is raw bytes where
        // every escape is load-bearing. A normalised URL asks the tracker about a
        // different torrent, or about nothing at all.
        Uri target = new(
            BuildUrl(url, request),
            new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });

        try
        {
            response = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, target), ct);
        }
        catch (HttpRequestException failure)
        {
            throw new TrackerException($"{url} could not be reached: {failure.Message}");
        }

        if (!response.IsSuccessStatusCode)
            throw new TrackerException($"{url} answered {(int)response.StatusCode}");

        byte[] body = await response.Content.ReadAsByteArrayAsync(ct);

        BValue parsed;

        try
        {
            parsed = BencodeReader.Parse(body);
        }
        catch (BencodeException failure)
        {
            throw new TrackerException($"{url} did not answer with bencode: {failure.Message}");
        }

        if (parsed is not BDictionary dictionary)
            throw new TrackerException($"{url} answered with something that is not a dictionary");

        if (dictionary.Entries.TryGetValue("failure reason", out BValue? failureReason) && failureReason is BBytes reason)
            throw new TrackerException($"{url} refused: {reason.AsText()}");

        TimeSpan interval = dictionary.Entries.TryGetValue("interval", out BValue? seconds) && seconds is BInteger number && number.Value > 0
            ? TimeSpan.FromSeconds(number.Value)
            : DefaultInterval;

        return new AnnounceResult(ReadPeers(dictionary, url), interval);
    }

    private static IReadOnlyList<PeerEndPoint> ReadPeers(BDictionary response, string url)
    {
        if (!response.Entries.TryGetValue("peers", out BValue? peers))
            return [];

        return peers switch
        {
            // The compact form: four bytes of address and two of port, repeated.
            BBytes compact => ReadCompact(compact.Value, url),

            // The original form, still served by some trackers.
            BList list => [.. list.Items.OfType<BDictionary>().Select(ReadDictionaryPeer).OfType<PeerEndPoint>()],

            _ => [],
        };
    }

    private static IReadOnlyList<PeerEndPoint> ReadCompact(byte[] compact, string url)
    {
        if (compact.Length % CompactEntryLength != 0)
            throw new TrackerException($"{url} sent a {compact.Length} byte peer list, which is not a multiple of {CompactEntryLength}");

        List<PeerEndPoint> peers = [];

        for (int offset = 0; offset < compact.Length; offset += CompactEntryLength)
        {
            IPAddress address = new(compact.AsSpan(offset, 4));
            int port = BinaryPrimitives.ReadUInt16BigEndian(compact.AsSpan(offset + 4, 2));

            if (port > 0)
                peers.Add(new PeerEndPoint(address, port));
        }

        return peers;
    }

    private static PeerEndPoint? ReadDictionaryPeer(BDictionary peer)
    {
        if (!peer.Entries.TryGetValue("ip", out BValue? ip) || ip is not BBytes address)
            return null;

        if (!peer.Entries.TryGetValue("port", out BValue? port) || port is not BInteger number)
            return null;

        return IPAddress.TryParse(address.AsText(), out IPAddress? parsed)
            ? new PeerEndPoint(parsed, (int)number.Value)
            : null;
    }

    private static string BuildUrl(string announceUrl, AnnounceRequest request)
    {
        StringBuilder url = new(announceUrl);

        // A private tracker's passkey usually already rides in the announce URL, so
        // append rather than assume this is the first parameter.
        url.Append(announceUrl.Contains('?') ? '&' : '?');

        url.Append("info_hash=").Append(Escape(request.InfoHash));
        url.Append("&peer_id=").Append(Escape(request.PeerId));
        url.Append("&port=").Append(request.Port);
        url.Append("&uploaded=").Append(request.Uploaded);
        url.Append("&downloaded=").Append(request.Downloaded);
        url.Append("&left=").Append(request.Left);
        url.Append("&compact=1");

        if (request.Event != AnnounceEvent.None)
            url.Append("&event=").Append(request.Event.ToString().ToLowerInvariant());

        return url.ToString();
    }

    /// <summary>
    /// Percent-encodes raw bytes by hand. An info hash is twenty arbitrary bytes, not
    /// text, and the usual URL helpers treat it as a string - which mangles exactly the
    /// bytes that happen to be printable and leaves the tracker answering about a
    /// torrent nobody has.
    /// </summary>
    private static string Escape(ReadOnlySpan<byte> bytes)
    {
        StringBuilder escaped = new(bytes.Length * 3);

        foreach (byte value in bytes)
        {
            bool unreserved = value is >= (byte)'A' and <= (byte)'Z'
                or >= (byte)'a' and <= (byte)'z'
                or >= (byte)'0' and <= (byte)'9'
                or (byte)'-' or (byte)'.' or (byte)'_' or (byte)'~';

            if (unreserved)
                escaped.Append((char)value);
            else
                escaped.Append('%').Append(value.ToString("x2"));
        }

        return escaped.ToString();
    }
}
