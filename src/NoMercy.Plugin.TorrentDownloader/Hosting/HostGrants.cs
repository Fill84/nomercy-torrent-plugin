using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Asks the owner for the hosts the manifest could not name.
/// </summary>
/// <remarks>
/// <strong>C2.</strong> A manifest cannot know a host the owner typed in, so
/// those are requested at runtime. 0.3.4 requested permission for the owner's
/// own indexers <em>only</em> — and on a default install, where there are none,
/// it therefore requested nothing at all while the pipeline searched the whole
/// shipped catalogue. Every refusal then read like the site refusing us.
///
/// The shipped hosts are not requested here, and that is deliberate: they are in
/// the manifest, which is how the server grants them.
/// </remarks>
public sealed class HostGrants(IPluginGrants grants, ILogger logger)
{
    /// <summary>The kind of grant a host needs.</summary>
    public const string NetworkHost = PluginGrantKind.NetworkHost;

    /// <summary>
    /// Asks for every host the plugin will actually reach that is not already
    /// granted, and says out loud which ones are being waited on.
    /// </summary>
    /// <remarks>
    /// Search addresses included. They are the half that gets forgotten, and a
    /// source whose feed is permitted and whose search is not fails only when
    /// it is asked a question — which is the one thing it was added for.
    /// </remarks>
    public async Task<IReadOnlyList<string>> RequestAsync(
        IEnumerable<SourceDefinition> ownSources,
        CancellationToken ct)
    {
        // Which sources reach each host, because two can share one and the
        // owner is deciding about the host. The names are what they read.
        SortedDictionary<string, SortedSet<string>> asking =
            new(StringComparer.OrdinalIgnoreCase);

        foreach (SourceDefinition source in ownSources)
        {
            foreach (string host in source.Hosts)
            {
                if (!asking.TryGetValue(host, out SortedSet<string>? names))
                {
                    names = new(StringComparer.OrdinalIgnoreCase);
                    asking[host] = names;
                }

                names.Add(source.Name);
            }
        }

        string[] hosts = [.. asking.Keys];

        List<string> waiting = [];

        foreach (string host in hosts)
        {
            if (await grants.HasAsync(NetworkHost, host, ct))
            {
                continue;
            }

            await grants.RequestAsync(
                NetworkHost,
                host,
                // The reason the owner reads when deciding. It names the
                // sources rather than the plugin, because "may this plugin
                // reach the internet" is not a question anybody can answer
                // well — and it says every source that shares the host, so
                // saying no is a decision about all of them.
                $"Searching {string.Join(", ", asking[host])} for missing episodes.",
                ct);

            waiting.Add(host);
        }

        if (waiting.Count > 0)
        {
            logger.LogWarning(
                "Waiting for permission to reach {Count} host(s): {Hosts}. Until then they are skipped, and that is not the site refusing.",
                waiting.Count,
                string.Join(", ", waiting));
        }

        return waiting;
    }
}
