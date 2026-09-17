using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// One indexer's answer to one question, a row's hash off its own page, and a torrent's every tracker.
/// </summary>
/// <remarks>
/// <para>
/// The site-facing half of the indexer round; what is asked, in what order, and which torrent wins is
/// <see cref="IndexerRound"/>'s (<c>docs/specs/indexer-search.md</c>).
/// </para>
/// <para>
/// <strong>A3:</strong> what goes out is a release name a name source gave, and nothing this plugin makes
/// up. The ladder of made-up questions — the episode, the season, the quality appended — and the merge by
/// name went on 15 September 2026 with the owner's requirements.
/// </para>
/// </remarks>
public sealed class Find(
    SourceCatalogue catalogue,
    IFetch fetch,
    Readers readers,
    IActivityJournal journal,
    ISourceLedger? ledger = null,
    TimeProvider? time = null,
    ISessionPost? post = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

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
    /// Over HTTP, in the session the listing was read in (<c>docs/07-solver.md</c>).
    /// Where nothing can post in that session this says so and changes nothing,
    /// which leaves the row without a hash to take part with.
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
            journal.Failed(ActivityStage.Find, subject, $"{route.Source} names its torrents only to a signed request, and nothing here can send one.");

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
    public async Task<ReleaseCopy[]> AskAsync(
        SourceDefinition indexer,
        SearchTerm term,
        string? about,
        CancellationToken ct,
        AskedThisCycle? asked = null)
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
                string said = failure.ToString();

                // A site that did not answer, or stayed behind its challenge, after the fetch's second attempt
                // sits out the rest of the run (run.md), and the Sources page says so beside its refusal.
                if (asked is not null && failure.Outcome is FetchOutcome.Unreachable or FetchOutcome.Challenged)
                {
                    asked.SitOut(indexer.Name);
                    said = $"{said} It is left out of the rest of this run.";
                }

                journal.Failed(ActivityStage.Find, subject, said);
                Said(about, indexer, term, said);
                await WroteAsync(indexer, started, 0, said, ct);

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
            // One site is one site, exactly as a feed is. An episode is worth
            // more than the indexer that failed on it.
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
            Published = row.Published,

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
