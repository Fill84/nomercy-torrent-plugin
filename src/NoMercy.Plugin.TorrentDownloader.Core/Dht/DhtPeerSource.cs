// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Trackers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Dht;

/// <summary>
/// Finding peers with no tracker at all.
///
/// <para>
/// The lookup walks towards the info hash: ask the closest nodes we know, they answer
/// with nodes closer still, and it repeats until somebody actually holds peers. That
/// is why the routing table's shape matters - a handful of hops instead of asking
/// everybody.
/// </para>
/// </summary>
public sealed class DhtPeerSource(NodeId self, RoutingTable table, IUdpTransport transport)
{
    /// <summary>Queries in flight per round. Three is what Kademlia suggests and every implementation uses.</summary>
    private const int Concurrency = 3;

    /// <summary>A lookup that has not converged by now is chasing a swarm that is not there.</summary>
    private const int MaxRounds = 8;

    public async Task<IReadOnlyList<PeerEndPoint>> FindPeersAsync(byte[] infoHash, CancellationToken ct)
    {
        NodeId target = new(infoHash);

        HashSet<PeerEndPoint> peers = [];
        HashSet<NodeId> asked = [];
        List<DhtNode> candidates = [.. table.Closest(target, RoutingTable.BucketSize)];

        for (int round = 0; round < MaxRounds && candidates.Count > 0; round++)
        {
            DhtNode[] batch = [.. candidates
                .Where(node => !asked.Contains(node.Id))
                .OrderBy(node => node.Id.DistanceTo(target))
                .Take(Concurrency)];

            if (batch.Length == 0)
                break;

            foreach (DhtNode node in batch)
                asked.Add(node.Id);

            KrpcResponse?[] answers = await Task.WhenAll(batch.Select(node => AskAsync(node, infoHash, ct)));

            foreach (KrpcResponse? answer in answers)
            {
                if (answer is null)
                    continue;

                foreach (PeerEndPoint peer in answer.Peers)
                    peers.Add(peer);

                foreach (DhtNode discovered in answer.Nodes)
                {
                    // Worth remembering beyond this lookup: a table that forgets everyone
                    // it met has to bootstrap from nothing every time.
                    table.Add(discovered);

                    if (!asked.Contains(discovered.Id))
                        candidates.Add(discovered);
                }
            }
        }

        return [.. peers];
    }

    /// <summary>
    /// Null for a node that did not answer or answered badly. On a DHT that is the
    /// steady state, not an incident - nodes go away without telling anybody.
    /// </summary>
    private async Task<KrpcResponse?> AskAsync(DhtNode node, byte[] infoHash, CancellationToken ct)
    {
        try
        {
            byte[] query = Krpc.GetPeers(self, infoHash, out _);

            byte[] answer = await transport.ExchangeAsync(
                node.EndPoint.Address.ToString(),
                node.EndPoint.Port,
                query,
                ct);

            return Krpc.ParseResponse(answer);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return null;
        }
    }
}
