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
    public Task<IReadOnlyList<ReleaseCopy>> SearchAsync(
        string releaseName,
        LibraryKind kind,
        CancellationToken ct)
    {
        // A release name, so letter for letter first and then without its
        // punctuation — the same two questions a cycle puts.
        return SearchAsync(SearchTerm.Ladder([releaseName], []), kind, ct);
    }

    /// <summary>
    /// Every copy anybody is serving, for questions this plugin made up.
    /// </summary>
    /// <remarks>
    /// Each goes out in the site's own style. A release name a source gave is
    /// not one of these: it goes through <see cref="SearchTerm.Ladder"/>, which
    /// asks it letter for letter first.
    /// </remarks>
    public Task<IReadOnlyList<ReleaseCopy>> SearchAsync(
        IReadOnlyList<string> ladder,
        LibraryKind kind,
        CancellationToken ct,
        AskedThisCycle? asked = null)
    {
        return SearchAsync([.. ladder.Select(rung => new SearchTerm(rung, false))], kind, ct, asked);
    }

    /// <summary>
    /// Every copy anybody is serving, each site asked the first question it can
    /// answer.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>A site that answers nothing was asked the wrong question, so it
    /// is asked a simpler one.</strong> The owner's rule, 10 September 2026, and
    /// it is measured: asked for <c>Silo.S03E06.1080p.WEB.H264-CAKES</c>, 1337x
    /// answers nothing at all and The Pirate Bay answers one row; one rung down,
    /// at <c>Silo S03E06 1080p</c>, 1337x answers four. It has the release the
    /// whole time and does not answer to the name it is published under.
    /// </para>
    /// <para>
    /// <strong>Each site climbs on its own, and that is the point.</strong> One
    /// ladder for all of them stops the moment anybody answers — so a site that
    /// needed one more rung is left with nothing, and the trackers it publishes
    /// for that torrent never reach the magnet. Asking every indexer is worth
    /// nothing if the ones that answer decide when the others stop being asked.
    /// </para>
    /// <para>
    /// A site stops at the rung it answers on. Climbing past it would spend a
    /// request on a question already answered and drag back the broader rows the
    /// ladder exists to avoid — the owner watched TorrentGalaxy answer a
    /// programme's own name with fifty rows of every season and every quality.
    /// </para>
    /// </remarks>
    /// <param name="ladder">
    /// The questions in order, narrowest first: the release names the sources
    /// know, then what the plugin makes up for itself.
    /// </param>
    /// <param name="kind">Which library, so a site is only asked what it serves.</param>
    /// <param name="ct">Cancellation.</param>
    /// <param name="asked">
    /// What each site has already answered this cycle, so no question is put
    /// to the same site twice. Null where there is no cycle to remember for.
    /// </param>
    /// <param name="about">
    /// The episode these questions are for, so every one of them — and what it
    /// came back with — is noted under it on the dashboard. Null where there is
    /// no episode to note it under.
    /// </param>
    public async Task<IReadOnlyList<ReleaseCopy>> SearchAsync(
        IReadOnlyList<SearchTerm> ladder,
        LibraryKind kind,
        CancellationToken ct,
        AskedThisCycle? asked = null,
        string? about = null)
    {
        // Only the indexers worth asking about this library. An anime-only site
        // asked about a television show spends a paced request on a site that
        // carries almost no television, and that request is taken from the ones
        // that would have answered.
        SourceDefinition[] indexers = [.. catalogue.For(SourceRole.Indexer).Where(one => one.Serves(kind))];

        ReleaseCopy[][] answers = await Task.WhenAll(
            indexers.Select(indexer => ClimbAsync(indexer, ladder, asked, about, ct)));

        return Merge([.. answers.SelectMany(answer => answer)]);
    }

    /// <summary>One site, asked down the ladder until it answers.</summary>
    private async Task<ReleaseCopy[]> ClimbAsync(
        SourceDefinition indexer,
        IReadOnlyList<SearchTerm> ladder,
        AskedThisCycle? asked,
        string? about,
        CancellationToken ct)
    {
        foreach (SearchTerm rung in ladder)
        {
            ReleaseCopy[]? rows = asked?.Recall(indexer.Name, rung);

            if (rows is null)
            {
                rows = await AskAsync(indexer, rung, about, ct);
                asked?.Keep(indexer.Name, rung, rows);
            }
            else
            {
                // Said, though nothing was sent: the page shows every question
                // this episode was answered by, and this one was answered for
                // another episode earlier in the run.
                Said(about, indexer, rung, $"already answered this run: {Rows(rows.Length)}");
            }

            if (rows.Length > 0)
            {
                return rows;
            }
        }

        return [];
    }

    /// <summary>
    /// The torrent, with every tracker that every indexer holding it knows.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>An indexer exists to hand over a torrent or a magnet, and the
    /// trackers are on that artefact.</strong> Not one shipped indexer prints a
    /// magnet on a listing — measured across every capture in
    /// <c>tests/fixtures/</c> and again live on 10 September 2026 — so the row's
    /// own page is the ordinary route to one, not an exception. Each indexer
    /// that has this torrent is asked for its artefact once, and every tracker
    /// of every one of them travels with the grab.
    /// </para>
    /// <para>
    /// <strong>What this replaces, and why it cost every download its
    /// trackers.</strong> This used to stop at the first thing that could be
    /// turned into a magnet: a row carrying an info hash was answered with
    /// <c>Magnets.For(hash, title)</c> — <c>magnet:?xt=urn:btih:…&amp;dn=…</c>,
    /// with no tracker in it — and its page was never read. Measured over four
    /// episodes and four sites, not one of a hundred and eighty merged torrents
    /// reached the client with a single tracker, so a swarm could only ever be
    /// found through the DHT. That is why torrents sat at "fetching metadata"
    /// with no peer and no seed.
    /// </para>
    /// <para>
    /// <strong>C3 still holds.</strong> This is one request per indexer for the
    /// release being taken, and none per row: following every row of every
    /// answer is a request per row per episode, which is how a plugin gets
    /// itself banned. All of them at once, because the gate paces each host on
    /// its own and one after another would cost the sum of the slowest sites on
    /// every download.
    /// </para>
    /// <para>
    /// A hash remains the fallback rather than a reason not to look. The Pirate
    /// Bay publishes a hash and no page at all, and it keeps working exactly as
    /// it did: where no route answers with an artefact, the magnet is still
    /// built from the hash.
    /// </para>
    /// </remarks>
    public async Task<ReleaseCopy> ResolveAsync(ReleaseCopy chosen, CancellationToken ct)
    {
        // A copy that never went through a merge — one built by a caller, or a
        // single row — still has the one route it is.
        CopyRoute[] routes = chosen.Routes.Count > 0
            ? [.. chosen.Routes]
            : [new CopyRoute(chosen.Source, chosen.DetailUrl, chosen.Magnet, chosen.Claim)];

        // The highest-rated site first, so its magnet is the one handed over
        // and its trackers lead. The owner's decision of 11 September 2026, made
        // for TorrentBay, which indexes every other site. Every route is still
        // asked, at once, and every tracker any of them names still travels:
        // this decides only whose answer the torrent is built on.
        routes = [.. routes.OrderByDescending(route => Rating(route.Source))];

        string?[] answered = await Task.WhenAll(routes.Select(route => ArtefactAsync(chosen, route, ct)));

        string[] found = [.. answered.OfType<string>()];

        // Everything anybody named for this torrent, once each. What the copy
        // already carried stays first: it was learned before this and the order
        // a tracker was learned in is what TrackerBook keeps.
        string[] trackers =
        [
            .. chosen.Trackers
                .Concat(Magnets.TrackersOf(chosen.Magnet))
                .Concat(found.SelectMany(Magnets.TrackersOf))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        string? magnet = chosen.Magnet ?? found.FirstOrDefault();
        string? hash = chosen.InfoHash ?? found.Select(Magnets.HashOf).OfType<string>().FirstOrDefault();

        if (magnet is null && hash is not null)
        {
            magnet = Magnets.For(hash, chosen.Title);
        }

        return chosen with
        {
            Magnet = magnet,
            InfoHash = hash,
            Trackers = trackers,
        };
    }

    /// <summary>
    /// The indexers that serve this library, in the order a round asks them: the first-choice indexers by
    /// their rank, then every other in catalogue order.
    /// </summary>
    /// <remarks>
    /// <c>docs/specs/indexer-search.md</c>: TorrentBay and LimeTorrents first for a show, Nyaa then those two
    /// for an anime. Nyaa serves only anime, so one rank each in <c>sources.json</c> gives both orders.
    /// </remarks>
    public IReadOnlyList<SourceDefinition> IndexersFor(LibraryKind kind)
    {
        SourceDefinition[] serving = [.. catalogue.For(SourceRole.Indexer).Where(one => one.Serves(kind))];

        return
        [
            .. serving.Where(one => one.FirstChoice is not null).OrderBy(one => one.FirstChoice),
            .. serving.Where(one => one.FirstChoice is null),
        ];
    }

    /// <summary>
    /// One row with the hash its own page names, when its listing named none.
    /// </summary>
    /// <remarks>
    /// Its page, or for a site that prints nothing its signed request — the same routes a winner is resolved
    /// through. A row whose torrent cannot be read comes back as it was, without a hash, and takes no part.
    /// </remarks>
    public async Task<ReleaseCopy> HashOfAsync(ReleaseCopy row, CancellationToken ct)
    {
        if (row.InfoHash is not null)
        {
            return row;
        }

        CopyRoute route = row.Routes.Count > 0 ? row.Routes[0] : new CopyRoute(row.Source, row.DetailUrl, row.Magnet, row.Claim);

        if (await ArtefactAsync(row, route, ct) is not string magnet || Magnets.HashOf(magnet) is not string hash)
        {
            return row;
        }

        return row with
        {
            Magnet = magnet,
            InfoHash = hash,
            Trackers = [.. row.Trackers.Concat(Magnets.TrackersOf(magnet)).Distinct(StringComparer.OrdinalIgnoreCase)],
            Routes = [route with { Magnet = magnet }],
        };
    }

    /// <summary>The catalogue's rating of a site, or nought for one it does not carry.</summary>
    private int Rating(string source)
    {
        return catalogue.Enabled
            .FirstOrDefault(one => string.Equals(one.Name, source, StringComparison.OrdinalIgnoreCase))
            ?.Priority ?? 0;
    }

    /// <summary>
    /// The magnet one indexer holds for this torrent, or null where it holds
    /// none.
    /// </summary>
    /// <remarks>
    /// A failure here is one site's failure and never the torrent's: the other
    /// routes are still being asked, and what they answer is enough.
    /// </remarks>
    private async Task<string?> ArtefactAsync(ReleaseCopy chosen, CopyRoute route, CancellationToken ct)
    {
        if (route.Magnet is not null)
        {
            // The listing carried it, so there is nothing to fetch. No shipped
            // indexer does this today, but the owner's own might.
            return route.Magnet;
        }

        if (route.Claim is SignedClaim claim && route.DetailUrl is not null)
        {
            // A site that prints no magnet and no hash anywhere, on the listing
            // or on the row's own page, and answers a signed request instead.
            // Asked instead of the page, because on this site the page has
            // nothing on it either and fetching it is a request spent for
            // certain on nothing.
            return await AskForMagnetAsync(chosen, route, claim, ct);
        }

        if (route.DetailUrl is null)
        {
            return null;
        }

        string subject = $"{chosen.Title} · {route.Source}";

        SourceDefinition? indexer = catalogue.Enabled
            .FirstOrDefault(source => string.Equals(source.Name, route.Source, StringComparison.OrdinalIgnoreCase));

        journal.Started(ActivityStage.Find, subject, "reading the row's own page for the torrent");

        try
        {
            FetchResult result = await fetch.GetAsync(route.DetailUrl, indexer?.Gated ?? false, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, subject, failure.ToString());

                return null;
            }

            if (DetailPage.Read(result.Body!, chosen.Title) is not (string magnet, string _))
            {
                journal.Failed(ActivityStage.Find, subject, "Its own page names no torrent either.");

                return null;
            }

            journal.Finished(
                ActivityStage.Find,
                subject,
                $"{Magnets.TrackersOf(magnet).Count} trackers");

            return magnet;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Find, subject, exception.Message);

            return null;
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
    private async Task<string?> AskForMagnetAsync(
        ReleaseCopy chosen,
        CopyRoute route,
        SignedClaim claim,
        CancellationToken ct)
    {
        string subject = $"{chosen.Title} · {route.Source}";

        if (post is null)
        {
            journal.Failed(ActivityStage.Find, subject, $"{route.Source} names its torrents only to a browser.");

            return null;
        }

        Uri endpoint = SignedMagnet.EndpointOn(route.DetailUrl!);

        journal.Started(ActivityStage.Find, subject, "asking the site for the torrent");

        try
        {
            string? answered = await post.PostAsync(
                endpoint,
                SignedMagnet.Body(claim, _time.GetUtcNow()),
                ct);

            if (SignedMagnet.MagnetIn(answered) is not string magnet)
            {
                journal.Failed(ActivityStage.Find, subject, $"{route.Source} would not name the torrent.");

                return null;
            }

            journal.Finished(ActivityStage.Find, subject, "magnet answered");

            return magnet;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            journal.Failed(ActivityStage.Find, subject, exception.Message);

            return null;
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

        // And then by hash, across names. The info hash is the identity: two
        // rows carrying it are the same bytes in the same swarm, and a site
        // writing the year in while another leaves it out has not found a
        // different file. Grouping by name alone kept those apart and their
        // trackers with them — two Lioness episodes sat at "fetching metadata"
        // with no peer and no seed while the same release seeded through a
        // tracker only the row that did not merge had published.
        //
        // After the pass above rather than instead of it: a row with no hash
        // can only be placed by its name, and that is what the name pass is
        // for. This puts together what the name pass could not see.
        List<ReleaseCopy> byHash = [];

        foreach (IGrouping<string?, ReleaseCopy> same in merged.GroupBy(copy =>
                     copy.InfoHash?.ToUpperInvariant()))
        {
            if (same.Key is null)
            {
                // Nothing identifies these as one another, so they stay as they
                // are rather than being guessed into a group.
                byHash.AddRange(same);

                continue;
            }

            byHash.Add(One(same));
        }

        return byHash;
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
            // The release as its group published it, not as a site printed it.
            // The site's own name on the end travels no further than this: it
            // is written against the grab, shown on every page, and matched
            // against the finished file by staging.
            Title = TitleMatcher.Clean(best.Title),
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
            // One per indexer, and every one of them kept. This is the whole
            // difference between a magnet with every site's trackers on it and
            // the bare one built from a hash that used to go out.
            Routes =
            [
                .. rows
                    .SelectMany(copy => copy.Routes)
                    .GroupBy(route => route.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(perSite => perSite.First()),
            ],
            Magnet = best.Magnet ?? rows.Select(copy => copy.Magnet).FirstOrDefault(magnet => magnet is not null),
            InfoHash = best.InfoHash ?? rows.Select(copy => copy.InfoHash).FirstOrDefault(hash => hash is not null),
            DetailUrl = best.DetailUrl ?? rows.Select(copy => copy.DetailUrl).FirstOrDefault(url => url is not null),
            SizeBytes = best.SizeBytes ?? rows.Select(copy => copy.SizeBytes).FirstOrDefault(size => size is not null),
            Claim = best.Claim ?? rows.Select(copy => copy.Claim).FirstOrDefault(claim => claim is not null),
        };
    }

    /// <summary>
    /// Notes what one indexer answered one question, under the episode it was
    /// asked for.
    /// </summary>
    private void Said(string? about, SourceDefinition indexer, SearchTerm term, string what)
    {
        if (about is not null)
        {
            journal.Noted(ActivityStage.Find, about, $"{indexer.Name} · {term.AsAsked} · {what}");
        }
    }

    private static string Rows(int count)
    {
        return count == 1 ? "1 row" : $"{count} rows";
    }

    /// <summary>One question to one indexer, every page it declares, and the rows it answered.</summary>
    public async Task<ReleaseCopy[]> AskAsync(SourceDefinition indexer, SearchTerm term, string? about, CancellationToken ct)
    {
        // As it went out, so the page shows the question the site was really
        // put: the name with its dots, or the words it was given instead.
        string subject = $"{term.AsAsked} · {indexer.Name}";
        long started = _time.GetTimestamp();

        journal.Started(ActivityStage.Find, subject);
        journal.Counted(RunCounter.Questions);

        try
        {
            Uri first = new(term.Exact
                ? Query.WriteExact(indexer.SearchAddress!, term.Text)
                : Query.Write(indexer.SearchAddress!, term.Text, indexer.Query));

            FetchResult result = await fetch.GetAsync(first, indexer.SearchAddressGated, ct);

            if (result.Failure is FetchFailure failure)
            {
                journal.Failed(ActivityStage.Find, subject, failure.ToString());
                Said(about, indexer, term, failure.ToString());
                await WroteAsync(indexer, started, 0, failure.ToString(), ct);

                return [];
            }

            ISourceReader? reader = readers.For(indexer);

            if (reader is null)
            {
                string unread = $"It answered and nothing here reads a source of kind '{indexer.Kind}'.";

                journal.Failed(ActivityStage.Find, subject, unread);
                Said(about, indexer, term, unread);
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

            if (copies.Count > 0)
            {
                journal.Counted(RunCounter.QuestionsAnswered);
            }

            Said(about, indexer, term, Rows(copies.Count));
            journal.Finished(ActivityStage.Find, subject, $"{copies.Count} copies");
            await WroteAsync(indexer, started, copies.Count, null, ct);

            return [.. copies];
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // One site is one site, exactly as it is in the harvest. An episode
            // is worth more than the indexer that failed on it.
            journal.Failed(ActivityStage.Find, subject, exception.Message);
            Said(about, indexer, term, exception.Message);
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

            // Its own route, before any merging. What survives a merge is the
            // union of these, so every site that has the torrent can be asked
            // for the artefact its trackers are on.
            Routes = [new(indexer.Name, row.DetailUrl, row.Magnet, row.Claim)],
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
