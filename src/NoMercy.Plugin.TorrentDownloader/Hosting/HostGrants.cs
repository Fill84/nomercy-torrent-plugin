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
    /// Asks for every host the owner's own sources reach that is not already
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
        string[] hosts =
        [
            .. ownSources
                .SelectMany(source => source.Hosts)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase),
        ];

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
                // The reason the owner reads when deciding. It names the source
                // rather than the plugin, because "may this plugin reach the
                // internet" is not a question anybody can answer well.
                $"Searching the indexer you added at {host}.",
                ct);

            waiting.Add(host);
        }

        if (waiting.Count > 0)
        {
            logger.LogWarning(
                "Waiting for permission to reach {Count} host(s) you configured: {Hosts}. Until then they are skipped, and that is not the site refusing.",
                waiting.Count,
                string.Join(", ", waiting));
        }

        return waiting;
    }
}
