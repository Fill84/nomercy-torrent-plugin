using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Asks every indexer who is serving one release, and merges what comes back.
/// </summary>
/// <remarks>
/// <strong>A3:</strong> what goes out is the full release name and nothing
/// else. 0.3.4 searched indexers for <c>Silo S03E06</c>, which sometimes
/// worked — and the times it did hid the times it did not.
/// </remarks>
public sealed class Find(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    IActivityJournal journal,
    ISourceLedger? ledger = null,
    TimeProvider? time = null,
    IInPagePost? post = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    /// <summary>
    /// Every copy of <paramref name="releaseName"/> anybody is serving.
    /// </summary>
    /// <remarks>
    /// Every indexer at once. Asked one after another, a search costs the sum
    /// of the slowest sites — and there is one search per episode of a cycle.
    /// The gate is the only thing that slows any of it down, and it does that
    /// per host.
    /// </remarks>
    public async Task<IReadOnlyList<ReleaseCopy>> SearchAsync(
        string releaseName,
        LibraryKind kind,
        CancellationToken ct)
    {
        // Only the indexers worth asking about this library. An anime-only site
        // asked about a television show spends a paced request on a site that
        // carries almost no television, and that request is taken from the ones
        // that would have answered.
        SourceDefinition[] indexers = [.. catalogue.For(SourceRole.Indexer).Where(one => one.Serves(kind))];

        ReleaseCopy[][] answers = await Task.WhenAll(
            indexers.Select(indexer => AskAsync(indexer, releaseName, ct)));

        return Merge([.. answers.SelectMany(answer => answer)]);
    }

    /// <summary>
    /// Follows a copy to its own page for the magnet the listing did not carry.
    /// </summary>
    /// <remarks>
    /// <strong>C3.</strong> Called for the copy that was chosen and for no
    /// other: following every row of every answer is a request per row per
    /// episode, which is how a plugin gets itself banned from a site. A copy
    /// that already has a magnet is answered as it is, without a request.
    /// </remarks>
    public async Task<ReleaseCopy> FollowAsync(ReleaseCopy chosen, CancellationToken ct)
    {
        if (chosen.Magnet is not null)
        {
            return chosen;
        }

        if (chosen.InfoHash is string known)
        {
            // A hash is all a magnet needs, so a copy that has one is already
            // reachable and its page has nothing to add. LimeTorrents publishes
            // a hashed .torrent link on every row and nothing else, and
            // following those pages would be a request per grab for a torrent
            // already in hand.
            //
            // Asked before the page address rather than after it. The Pirate
            // Bay's own endpoint answers with a hash and no page at all, and
            // while the two were the other way round every row it gave came
            // back unreachable: the highest-priority indexer in the catalogue,
            // with the most honest seeder counts of any of them, could not
            // produce a single download.
            return chosen with { Magnet = Magnets.For(known, chosen.Title) };
        }

        if (chosen.Claim is SignedClaim claim && chosen.DetailUrl is not null)
        {
            // A site that prints no magnet and no hash anywhere, on the listing
            // or on the row's own page, and answers a signed request instead.
            // Asked before the page is fetched, because on this site the page
            // has nothing on it either and fetching it is a request spent for
            // certain on nothing.
            return await AskForMagnetAsync(chosen, claim, ct);
        }

        if (chosen.DetailUrl is null)
        {
            // No magnet, no hash and no page to look on. Nothing more can be
            // done for it here.
            return chosen;
        }

        SourceDefinition? indexer = catalogue.Enabled
            .FirstOrDefault(source => string.Equals(source.Name, chosen.Source, StringComparison.OrdinalIgnoreCase));

        journal.Started(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", "following the row's own page");

        try
        {
            FetchResult result = await fetch.GetAsync(chosen.DetailUrl, indexer?.Gated ?? false, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", failure.ToString());

                return chosen;
            }

            if (DetailPage.Read(result.Body!, chosen.Title) is not (string magnet, string hash))
            {
                journal.Failed(
                    ActivityStage.Find,
                    $"{chosen.Title} · {chosen.Source}",
                    "Its own page names no torrent either.");

                return chosen;
            }

            journal.Finished(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", "magnet found");

            return chosen with
            {
                Magnet = magnet,
                InfoHash = chosen.InfoHash ?? hash,
                Trackers = [.. chosen.Trackers.Union(Magnets.TrackersOf(magnet), StringComparer.OrdinalIgnoreCase)],
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Find, $"{chosen.Title} · {chosen.Source}", exception.Message);

            return chosen;
        }
    }

    /// <summary>
    /// Asks a site for a torrent it will not print, and answers the copy with
    /// the magnet on it.
    /// </summary>
    /// <remarks>
    /// From inside the browser session that loaded the page. Sent from this
    /// process the request arrives without the session that earned the right to
    /// ask, and is refused — so where there is no browser this says so and
    /// changes nothing, which leaves the caller free to try the next copy.
    /// </remarks>
    private async Task<ReleaseCopy> AskForMagnetAsync(
        ReleaseCopy chosen,
        SignedClaim claim,
        CancellationToken ct)
    {
        string subject = $"{chosen.Title} · {chosen.Source}";

        if (post is null)
        {
            journal.Failed(ActivityStage.Find, subject, $"{chosen.Source} names its torrents only to a browser.");

            return chosen;
        }

        Uri endpoint = SignedMagnet.EndpointOn(chosen.DetailUrl!);

        journal.Started(ActivityStage.Find, subject, "asking the site for the torrent");

        try
        {
            string? answered = await post.PostAsync(
                endpoint,
                SignedMagnet.Body(claim, _time.GetUtcNow()),
                ct);

            if (SignedMagnet.MagnetIn(answered) is not string magnet)
            {
                journal.Failed(ActivityStage.Find, subject, $"{chosen.Source} would not name the torrent.");

                return chosen;
            }

            journal.Finished(ActivityStage.Find, subject, "magnet answered");

            return chosen with
            {
                Magnet = magnet,
                InfoHash = chosen.InfoHash ?? Magnets.HashOf(magnet),
                Trackers = [.. chosen.Trackers.Union(Magnets.TrackersOf(magnet), StringComparer.OrdinalIgnoreCase)],
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Find, subject, exception.Message);

            return chosen;
        }
    }

    /// <summary>
    /// One torrent per release, with everything every site knew about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A scene release name is the file's identity — the group, the resolution
    /// and the source are all in it — so two rows carrying that name are one
    /// torrent however differently the sites punctuate it. They are merged, and
    /// the count that survives is the highest any of them gave, because how
    /// many are serving a torrent is a property of the swarm and not of the
    /// site that was asked.
    /// </para>
    /// <para>
    /// <strong>That is not a tidying-up.</strong> On the owner's own library on
    /// 22 August 2026 TorrentBay offered
    /// <c>Sugar (2024) S02E08 1080p Web h264 Cakes</c> and said one seeder, so
    /// it was refused for being under the minimum — while the same release was
    /// seeded in the thousands everywhere else. Merging was by info hash alone
    /// and that site publishes none, so nothing could rescue it, and the cycle
    /// took a different release the owner did not want.
    /// </para>
    /// <para>
    /// Two <em>different</em> hashes under one name are still two files, and
    /// they stay two: merging those would hand one torrent's trackers to
    /// another. The copy that survives a merge is one that can actually be
    /// reached, because the best-informed row is no use if it names nothing to
    /// download.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<ReleaseCopy> Merge(IReadOnlyList<ReleaseCopy> copies)
    {
        List<ReleaseCopy> merged = [];

        foreach (IGrouping<string, ReleaseCopy> named in copies.GroupBy(copy => TitleMatcher.Release(copy.Title)))
        {
            string[] hashes =
            [
                .. named
                    .Select(copy => copy.InfoHash)
                    .OfType<string>()
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ];

            if (hashes.Length > 1)
            {
                // One name over two files. Nothing here can say which of them a
                // row with no hash belongs to, so each hash is its own torrent
                // and the rest are left exactly as they arrived.
                merged.AddRange(named.Where(copy => copy.InfoHash is null));
                merged.AddRange(hashes.Select(hash => One(named.Where(copy =>
                    string.Equals(copy.InfoHash, hash, StringComparison.OrdinalIgnoreCase)))));

                continue;
            }

            merged.Add(One(named));
        }

        return merged;
    }

    /// <summary>One torrent out of every row that named it.</summary>
    private static ReleaseCopy One(IEnumerable<ReleaseCopy> same)
    {
        ReleaseCopy[] rows = [.. same];

        // Reachable first: a copy with a route to the torrent, and among those
        // the site that knew the most about it. A row that names nothing to
        // download cannot be the one that is handed over, however well
        // informed it was.
        ReleaseCopy best = rows
            .OrderByDescending(copy => copy.Magnet is not null || copy.InfoHash is not null)
            .ThenByDescending(copy => copy.Seeders ?? 0)
            .ThenByDescending(copy => copy.Priority)
            .First();

        // The count belongs to the swarm. Null stays null only when not one
        // site gave a number: nought is not the same as nobody saying.
        int? seeders = rows.Select(copy => copy.Seeders).OfType<int>().DefaultIfEmpty().Max();

        return best with
        {
            Seeders = rows.Any(copy => copy.Seeders is not null) ? seeders : null,
            Source = rows
                .OrderByDescending(copy => copy.Seeders ?? -1)
                .ThenByDescending(copy => copy.Priority)
                .First()
                .Source,
            Trackers =
            [
                .. rows
                    .SelectMany(copy => copy.Trackers.Union(Magnets.TrackersOf(copy.Magnet), StringComparer.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase),
            ],
            Magnet = best.Magnet ?? rows.Select(copy => copy.Magnet).FirstOrDefault(magnet => magnet is not null),
            InfoHash = best.InfoHash ?? rows.Select(copy => copy.InfoHash).FirstOrDefault(hash => hash is not null),
            DetailUrl = best.DetailUrl ?? rows.Select(copy => copy.DetailUrl).FirstOrDefault(url => url is not null),
            SizeBytes = best.SizeBytes ?? rows.Select(copy => copy.SizeBytes).FirstOrDefault(size => size is not null),
            Claim = best.Claim ?? rows.Select(copy => copy.Claim).FirstOrDefault(claim => claim is not null),
        };
    }

    private async Task<ReleaseCopy[]> AskAsync(SourceDefinition indexer, string releaseName, CancellationToken ct)
    {
        string subject = $"{releaseName} · {indexer.Name}";
        long started = _time.GetTimestamp();

        journal.Started(ActivityStage.Find, subject);

        try
        {
            Uri first = new(Query.Write(indexer.SearchAddress!, releaseName, indexer.Query));

            FetchResult result = await fetch.GetAsync(first, indexer.SearchAddressGated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, subject, failure.ToString());
                await WroteAsync(indexer, started, 0, failure.ToString(), ct);

                return [];
            }

            ISourceReader? reader = readers.For(indexer);

            if (reader is null)
            {
                string unread = $"It answered and nothing here reads a source of kind '{indexer.Kind}'.";

                journal.Failed(ActivityStage.Find, subject, unread);
                await WroteAsync(indexer, started, 0, unread, ct);

                return [];
            }

            List<ReleaseCopy> copies = [.. CopiesIn(reader, result.Body!, first, indexer)];

            // The pages after the first, for a site that declares it has them.
            // A listing that answers seventy-one results fifty to a page keeps
            // the other twenty-one somewhere, and the release the owner wants
            // is as likely to be among them as not.
            foreach (Uri next in NextPages(indexer, first))
            {
                FetchResult page = await fetch.GetAsync(next, indexer.SearchAddressGated, ct);

                if (page.Failure is not null)
                {
                    // One page is one page. What the earlier ones answered is
                    // still worth having, and a site that stops answering
                    // half way through is not a site that answered nothing.
                    journal.Failed(ActivityStage.Find, subject, page.Failure.ToString());

                    break;
                }

                ReleaseCopy[] more = [.. CopiesIn(reader, page.Body!, next, indexer)];

                if (more.Length == 0)
                {
                    // Past the end. Asking for the page after the last is a
                    // request spent on a page nobody wrote.
                    break;
                }

                copies.AddRange(more);
            }

            journal.Finished(ActivityStage.Find, subject, $"{copies.Count} copies");
            await WroteAsync(indexer, started, copies.Count, null, ct);

            return [.. copies];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One site is one site, exactly as it is in the harvest. An episode
            // is worth more than the indexer that failed on it.
            journal.Failed(ActivityStage.Find, subject, exception.Message);
            await WroteAsync(indexer, started, 0, exception.Message, ct);

            return [];
        }
    }

    /// <summary>What one page of one site's answer holds.</summary>
    private static IEnumerable<ReleaseCopy> CopiesIn(
        ISourceReader reader,
        string body,
        Uri address,
        SourceDefinition indexer)
    {
        return reader.Read(body, address).Select(row => new ReleaseCopy(
            row.Title,
            indexer.Name,
            indexer.Priority,
            row.InfoHash ?? Magnets.HashOf(row.Magnet),
            row.Magnet,
            row.DetailUrl,
            row.Seeders,
            row.SizeBytes,
            Magnets.TrackersOf(row.Magnet))
        {
            Claim = row.Claim,
        });
    }

    /// <summary>
    /// The addresses of the pages after the first, for a site that declares it
    /// paginates.
    /// </summary>
    /// <remarks>
    /// Declared rather than discovered: the parameter is the site's own, and a
    /// guess at it is a request that fetches page one again and reads every row
    /// of it twice.
    /// </remarks>
    private static IEnumerable<Uri> NextPages(SourceDefinition indexer, Uri first)
    {
        if (indexer.PageParameter is not string parameter || indexer.Pages <= 1)
        {
            yield break;
        }

        string join = first.Query.Length > 0 ? "&" : "?";

        for (int page = 2; page <= indexer.Pages; page++)
        {
            yield return new($"{first}{join}{parameter}={page}");
        }
    }

    /// <summary>
    /// Writes down what one source answered, for the Sources page.
    /// </summary>
    /// <remarks>
    /// Nought rows with no refusal and nought rows with one are two different
    /// answers, and both are written: a site that answered and had nothing is a
    /// working site. Reporting a site's own rate limit as a broken reader is
    /// <strong>G2</strong>, and keeping the refusal in the site's own words is
    /// what stops it.
    /// </remarks>
    private async Task WroteAsync(
        SourceDefinition indexer,
        long started,
        int rows,
        string? refusal,
        CancellationToken ct)
    {
        if (ledger is null)
        {
            return;
        }

        await ledger.RecordAsync(
            new(indexer.Name, _time.GetUtcNow(), rows, refusal, _time.GetElapsedTime(started)),
            ct);
    }
}
