// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using NoMercy.Plugin.TorrentDownloader.Core.Indexers;
using NoMercy.Plugin.TorrentDownloader.Core.Releases;

namespace NoMercy.Plugin.TorrentDownloader.Core.Orchestration;

/// <summary>
/// Turning a release that was announced into one that can be downloaded.
///
/// <para>
/// A feed says a release exists and what it is called. It carries no torrent, and that is
/// not an oversight - a scene announcement is a notice board, not a shop. Without this
/// step the plugin matches an episode perfectly and then has nothing to hand the engine,
/// which is the "matched, grabbed nothing" case that looks from outside exactly like a
/// feed that found nothing at all.
/// </para>
/// </summary>
public interface IReleaseResolver
{
    /// <summary>
    /// Where to get <paramref name="announced"/>, or null when nobody has it.
    ///
    /// <para>
    /// Returns a release rather than a URL because the answer carries its own seeders,
    /// hash and trackers, and those belong to whoever is actually serving it - not to the
    /// site that merely said it existed.
    /// </para>
    /// </summary>
    Task<ReleaseInfo?> ResolveAsync(ReleaseInfo announced, CancellationToken ct);
}

/// <summary>
/// Looks the announced name up on the sites the owner configured.
///
/// <para>
/// The ranking is the important part, and it is not the quality profile. The profile has
/// already decided <em>which release</em> is wanted; this only decides <em>where to get
/// that one</em>. So a row whose title is exactly the announced release wins outright,
/// even over a better-seeded row of something else - a different release is not a better
/// answer to this question, it is an answer to a different question.
/// </para>
/// </summary>
public sealed class IndexerReleaseResolver(IReadOnlyList<PacedIndexer> sites) : IReleaseResolver
{
    public async Task<ReleaseInfo?> ResolveAsync(ReleaseInfo announced, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(announced.Title))
            return null;

        // Searched by the exact announced name, because that name is the whole reason this
        // can be trusted. Searching by show and episode instead would return every version
        // of the episode and put the choice back where it has already been made.
        //
        // Asked of each site rather than through the aggregator, which merges rows that
        // share a title. Here they are the answer: two sites offering the same release is
        // the ordinary case, and the choice between them is what this exists to make.
        SearchQuery query = new(announced.Title);

        ReleaseInfo[][] perSite = await Task.WhenAll(sites.Select(async site =>
        {
            try
            {
                // Through the pacer, so a resolve obeys the same rate limit a search does.
                // A site is one site however many reasons this plugin has to ask it.
                return (await site.Pacer.RunAsync(
                    token => site.Indexer.SearchAsync(query, token), ct)).ToArray();
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                // One site being down is not the others being down. A resolve that finds
                // it somewhere is a resolve that worked.
                return [];
            }
        }));

        IReadOnlyList<ReleaseInfo> found = [.. perSite.SelectMany(rows => rows)];

        List<ReleaseInfo> usable =
        [
            .. found.Where(candidate => candidate.MagnetUri is not null || candidate.DownloadUrl is not null),
        ];

        if (usable.Count == 0)
            return null;

        string wanted = TitleMatcher.Normalize(announced.Title);

        return usable
            .OrderBy(candidate => TitleMatcher.Normalize(candidate.Title) == wanted ? 0 : 1)
            .ThenBy(candidate => candidate.IndexerPriority)
            .ThenByDescending(candidate => candidate.Seeders)
            .First();
    }
}
