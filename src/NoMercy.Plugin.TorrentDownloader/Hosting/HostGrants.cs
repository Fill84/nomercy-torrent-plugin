// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Configuration;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

// The manifest cannot name a user-configured host, so this asks for it at runtime instead.
// Safe to call on every scheduled tick: RequestAsync records and returns immediately, and
// asking twice for a host already requested is not an error and does not queue a second
// prompt to the owner - see IPluginGrants.RequestAsync.
public sealed class HostGrants(IPluginGrants grants)
{
    private const string ReasonFormat = "Torrent Downloader needs to reach the indexer you configured at {0}.";

    public async Task<IReadOnlyList<string>> EnsureAsync(TorrentDownloaderSettings settings, CancellationToken ct = default)
    {
        HashSet<string> hosts = [];

        foreach (IndexerSettings indexer in settings.Indexers)
        {
            if (indexer.Enabled && TryGetHost(indexer.Url, out string indexerHost))
            {
                hosts.Add(indexerHost);
            }
        }

        foreach (TorrentClientSettings client in settings.Clients)
        {
            if (client.Enabled && TryGetHost(client.Url, out string clientHost))
            {
                hosts.Add(clientHost);
            }
        }

        if (hosts.Count == 0)
        {
            return [];
        }

        bool holdsEverything = await grants.HasAsync(PluginGrantKind.NetworkHost, PluginGrant.Everything, ct);
        if (holdsEverything)
        {
            return [];
        }

        List<string> ungranted = [];
        foreach (string host in hosts)
        {
            bool granted = await grants.HasAsync(PluginGrantKind.NetworkHost, host, ct);
            if (granted)
            {
                continue;
            }

            string reason = string.Format(ReasonFormat, host);
            await grants.RequestAsync(PluginGrantKind.NetworkHost, host, reason, ct);
            ungranted.Add(host);
        }

        return ungranted;
    }

    // A half-filled settings form must not break the tick: an empty, malformed, or
    // host-less URL (e.g. "mailto:someone@example.com") is skipped, never thrown.
    private static bool TryGetHost(string url, out string host)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed) && !string.IsNullOrEmpty(parsed.Host))
        {
            host = parsed.Host;
            return true;
        }

        host = string.Empty;
        return false;
    }
}
