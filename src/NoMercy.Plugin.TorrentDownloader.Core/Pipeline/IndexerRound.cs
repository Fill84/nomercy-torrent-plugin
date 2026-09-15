using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>One indexer's row whose title is a searched release name, and where in the round it was found.</summary>
/// <param name="Row">The row, with its hash when the listing or the row's own page gave one.</param>
/// <param name="Name">The release name it was found for, as the name source wrote it.</param>
/// <param name="FoundAt">
/// Its place in the round: the indexer's place in the order it was asked, then the row's place on that
/// indexer's page. Lower was found first.
/// </param>
public sealed record FoundRow(ReleaseCopy Row, string Name, int FoundAt);

/// <summary>One torrent, merged from every row of its info hash.</summary>
/// <param name="Torrent">The torrent, carrying every tracker and every indexer's route.</param>
/// <param name="Indexers">Every indexer that listed it, each once, in the order they were asked.</param>
/// <param name="FoundAt">Where the first of its rows was found.</param>
public sealed record RankedTorrent(ReleaseCopy Torrent, IReadOnlyList<string> Indexers, int FoundAt);

/// <summary>Rows of one info hash as one torrent.</summary>
/// <remarks>
/// <c>docs/specs/indexer-search.md</c> § Merging by hash: results with the same info hash, from any number of
/// indexers, are one torrent, whose magnet carries every tracker any of them gave — whichever torrent wins.
/// A row with no hash, because neither its listing nor its own page named one, cannot be placed and is part
/// of no torrent. Merging by name is gone: two uploads of one release are two torrents.
/// </remarks>
public static class Merging
{
    /// <summary>How far apart two indexers' places are, so a page of rows never reaches the next indexer's.</summary>
    public const int PerIndexer = 1000;

    public static IReadOnlyList<RankedTorrent> ByHash(IReadOnlyList<FoundRow> found)
    {
        return
        [
            .. found
                .Where(one => one.Row.InfoHash is not null)
                .GroupBy(one => one.Row.InfoHash!.ToUpperInvariant())
                .Select(One),
        ];
    }

    private static RankedTorrent One(IGrouping<string, FoundRow> same)
    {
        FoundRow[] rows = [.. same.OrderBy(one => one.FoundAt)];
        FoundRow first = rows[0];

        string[] trackers =
        [
            .. rows
                .SelectMany(one => one.Row.Trackers.Concat(Magnets.TrackersOf(one.Row.Magnet)))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        ReleaseCopy torrent = first.Row with
        {
            // Under the name the name source published: it is written against the grab and staging finds the
            // finished file by it, and a site's own rendering is a name nothing answers to.
            Title = first.Name,
            InfoHash = same.Key,
            Magnet = rows.Select(one => one.Row.Magnet).FirstOrDefault(magnet => magnet is not null) ?? Magnets.For(same.Key, first.Name),
            Seeders = rows.Any(one => one.Row.Seeders is not null) ? rows.Max(one => one.Row.Seeders ?? 0) : null,
            Trackers = trackers,
            Routes =
            [
                .. rows
                    .SelectMany(one => one.Row.Routes)
                    .GroupBy(route => route.Source, StringComparer.OrdinalIgnoreCase)
                    .Select(perSite => perSite.First()),
            ],
        };

        return new(torrent, [.. rows.Select(one => one.Row.Source).Distinct(StringComparer.OrdinalIgnoreCase)], first.FoundAt);
    }
}

/// <summary>Which merged torrent wins, and the order the rest would follow in.</summary>
/// <remarks>
/// <para>
/// <c>docs/specs/indexer-search.md</c> § The winner: the torrent found on the most indexers wins; level on
/// indexers, the one found on a first-choice indexer — for an anime, on Nyaa before TorrentBay or
/// LimeTorrents — and still level, the one found first.
/// </para>
/// <para>
/// <strong>The middle rule is the last one, and deliberately one sort key.</strong> The first-choice
/// indexers are asked first and in their order, Nyaa ahead of the rest for an anime, so a torrent found on
/// one of them was found before any torrent found only elsewhere. Found first says both.
/// </para>
/// <para>
/// A release or hash the download client failed and that is still refused takes no part. Seeders and a
/// site's rating decide nothing any more.
/// </para>
/// </remarks>
public static class Winner
{
    public static IReadOnlyList<RankedTorrent> Order(IReadOnlyList<RankedTorrent> merged, IReadOnlySet<string> blacklisted)
    {
        return
        [
            .. merged
                .Where(one => !Refused(one, blacklisted))
                .OrderByDescending(one => one.Indexers.Count)
                .ThenBy(one => one.FoundAt),
        ];
    }

    private static bool Refused(RankedTorrent torrent, IReadOnlySet<string> blacklisted)
    {
        return (torrent.Torrent.InfoHash is string hash
                && (blacklisted.Contains(hash) || blacklisted.Contains(hash.ToUpperInvariant()) || blacklisted.Contains(hash.ToLowerInvariant())))
               || blacklisted.Contains(Blacklist.KeyOf(torrent.Torrent.Title));
    }
}

/// <summary>
/// One wish group's release names put to every indexer that serves the library, and the torrents found,
/// merged and in winning order.
/// </summary>
/// <remarks>
/// <para>
/// <c>docs/specs/indexer-search.md</c> § Asking the indexers and <c>run.md</c>. The first-choice indexers —
/// TorrentBay and LimeTorrents for a show, Nyaa then those two for an anime, <c>firstChoice</c> in
/// <c>sources.json</c> — are asked one after another, and then every other enabled indexer at the same time.
/// A result on a first-choice indexer does not end the round: every indexer is asked, because the torrent on
/// the most indexers wins.
/// </para>
/// <para>
/// Each name is asked letter for letter, and an indexer with nothing for it is asked the name without its
/// punctuation. A row counts only when its title is the name, and it is not judged against the show's
/// settings: the name already was.
/// </para>
/// <para>
/// <strong>A row that counts and carries no hash has its own page read for one.</strong> Most indexers print
/// no hash on a listing, and without it the row cannot be merged or counted. Only rows whose title is the
/// name are read — one or two an indexer — which is what keeps C3's rule: no request per row of an answer.
/// </para>
/// </remarks>
public sealed class IndexerRound(Find find, IActivityJournal journal)
{
    public async Task<IReadOnlyList<RankedTorrent>> AskAsync(
        IReadOnlyList<string> names,
        TrackedEpisode episode,
        IReadOnlySet<string> blacklisted,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        IReadOnlyList<SourceDefinition> order = find.IndexersFor(episode.Kind);
        string about = $"{episode.ShowTitle} {episode.Key}";
        List<FoundRow> found = [];

        for (int place = 0; place < order.Count; place++)
        {
            if (order[place].FirstChoice is not null)
            {
                // One after another, and each finished before the next is asked.
                found.AddRange(Placed(place, await IndexerAsync(order[place], names, asked, about, ct)));
            }
        }

        int[] rest = [.. Enumerable.Range(0, order.Count).Where(place => order[place].FirstChoice is null)];

        (string Name, ReleaseCopy Row)[][] answers =
            await Task.WhenAll(rest.Select(place => IndexerAsync(order[place], names, asked, about, ct)));

        for (int at = 0; at < rest.Length; at++)
        {
            found.AddRange(Placed(rest[at], answers[at]));
        }

        foreach (FoundRow unreadable in found.Where(one => one.Row.InfoHash is null))
        {
            journal.Noted(ActivityStage.Find, about, $"{unreadable.Row.Source} · {unreadable.Row.Title} · its torrent could not be read, so it takes no part");
        }

        return Winner.Order(Merging.ByHash(found), blacklisted);
    }

    private static IEnumerable<FoundRow> Placed(int place, (string Name, ReleaseCopy Row)[] rows)
    {
        return rows.Select((one, at) => new FoundRow(one.Row, one.Name, (place * Merging.PerIndexer) + at));
    }

    /// <summary>One indexer, asked every name of the group: exactly, and without punctuation where that found nothing.</summary>
    private async Task<(string Name, ReleaseCopy Row)[]> IndexerAsync(
        SourceDefinition indexer,
        IReadOnlyList<string> names,
        AskedThisCycle asked,
        string about,
        CancellationToken ct)
    {
        List<(string Name, ReleaseCopy Row)> counted = [];

        foreach (string name in names)
        {
            foreach (SearchTerm term in (SearchTerm[])[new(name, true), new(name, false)])
            {
                ReleaseCopy[] rows = asked.Recall(indexer.Name, term) ?? await AskAsync(indexer, term, name, about, asked, ct);
                ReleaseCopy[] named = [.. rows.Where(row => IsTheName(row.Title, name))];

                if (named.Length > 0)
                {
                    counted.AddRange(named.Select(row => (name, row)));

                    break;
                }
            }
        }

        return [.. counted];
    }

    /// <summary>Asks once, reads a hash for every row that is the name and carries none, and keeps the answer for the run.</summary>
    private async Task<ReleaseCopy[]> AskAsync(
        SourceDefinition indexer,
        SearchTerm term,
        string name,
        string about,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        ReleaseCopy[] rows = await find.AskAsync(indexer, term, about, ct);

        for (int at = 0; at < rows.Length; at++)
        {
            if (rows[at].InfoHash is null && IsTheName(rows[at].Title, name))
            {
                rows[at] = await find.HashOfAsync(rows[at], ct);
            }
        }

        // Kept resolved, so the same question later in the run reads no page again either.
        asked.Keep(indexer.Name, term, rows);

        return rows;
    }

    /// <summary>
    /// Whether a row's title is the release name: the same letters and digits in the same order, with case,
    /// punctuation and the site's own tag set aside.
    /// </summary>
    public static bool IsTheName(string title, string name)
    {
        return string.Equals(TitleMatcher.Release(title), TitleMatcher.Release(name), StringComparison.Ordinal);
    }
}
