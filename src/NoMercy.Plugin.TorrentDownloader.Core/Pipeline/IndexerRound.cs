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
    /// <summary>One thing put to every indexer, which of its rows count, and the name a counting row goes by.</summary>
    /// <param name="Text">What is asked: letter for letter first, then without punctuation.</param>
    /// <param name="Counts">Whether a row's title counts for this question.</param>
    /// <param name="NameOf">The release name a counting row is merged and downloaded under.</param>
    /// <param name="Aired">
    /// The episode whose air date a row is held against, when its title alone cannot say which programme it
    /// is of; null when the title is a release name that was already judged by its own date.
    /// </param>
    private sealed record Question(string Text, Func<string, bool> Counts, Func<ReleaseCopy, string> NameOf, TrackedEpisode? Aired = null)
    {
        /// <summary>Whether a row whose title counts was uploaded too long before the episode aired to be of it.</summary>
        public bool UploadedBeforeItAired(ReleaseCopy row)
        {
            return Aired is TrackedEpisode episode && SourceName.PublishedBeforeItAired(row.Published, episode);
        }

        /// <summary>Whether a row counts for this question: its title, and when it was uploaded.</summary>
        public bool Takes(ReleaseCopy row)
        {
            return Counts(row.Title) && !UploadedBeforeItAired(row);
        }
    }

    public Task<IReadOnlyList<RankedTorrent>> AskAsync(
        IReadOnlyList<string> names,
        TrackedEpisode episode,
        IReadOnlySet<string> blacklisted,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        Question[] questions =
        [
            .. names.Select(name => new Question(name, title => IsTheName(title, name), _ => name)),
        ];

        return RoundAsync(questions, episode, blacklisted, asked, ct);
    }

    /// <summary>
    /// The show and episode put to every indexer, when no release name gave a torrent.
    /// </summary>
    /// <remarks>
    /// <c>docs/specs/indexer-search.md</c> § When no release name gives a torrent — the owner's rule of
    /// 16 September 2026. South Park S15E12 had one scene name that met its show's settings, a 2012 release no
    /// indexer has a torrent for, and the release the indexers do have was never asked for. A row counts only
    /// when <paramref name="meets"/> says it names this episode and meets the show's settings, and it goes by
    /// its own title: no name source gave it one.
    /// <para>
    /// A row its indexer dates more than <see cref="SourceName.Before"/> before the episode aired does not
    /// count. Two programmes share the title Dark Matter, and on 17 September 2026 this search took the 2015
    /// programme's S02E04 remux, uploaded that February, for the 2024 programme's S02E04 aired that day: the
    /// title carries no year, and when it was uploaded is the one thing that differs. A row with no date
    /// counts as its title says.
    /// </para>
    /// </remarks>
    /// <param name="episode">The episode asked for, by its show's title, season and episode.</param>
    /// <param name="meets">Whether a row's title names the episode and meets the show's settings.</param>
    /// <param name="blacklisted">Releases and hashes still refused, which take no part.</param>
    /// <param name="asked">What each indexer has already answered this run.</param>
    /// <param name="ct">The run's lifetime.</param>
    public Task<IReadOnlyList<RankedTorrent>> AskForEpisodeAsync(
        TrackedEpisode episode,
        Func<string, bool> meets,
        IReadOnlySet<string> blacklisted,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        Question[] questions = [new($"{episode.ShowTitle} {episode.Key}", meets, row => row.Title, episode)];

        return RoundAsync(questions, episode, blacklisted, asked, ct);
    }

    private async Task<IReadOnlyList<RankedTorrent>> RoundAsync(
        IReadOnlyList<Question> names,
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

    /// <summary>One indexer, asked every question of the group: exactly, and without punctuation where that found nothing.</summary>
    private async Task<(string Name, ReleaseCopy Row)[]> IndexerAsync(
        SourceDefinition indexer,
        IReadOnlyList<Question> questions,
        AskedThisCycle asked,
        string about,
        CancellationToken ct)
    {
        List<(string Name, ReleaseCopy Row)> counted = [];

        foreach (Question question in questions)
        {
            foreach (SearchTerm term in (SearchTerm[])[new(question.Text, true), new(question.Text, false)])
            {
                if (asked.SitsOut(indexer.Name))
                {
                    // Left out of the rest of the run after it gave no answer twice.
                    return [.. counted];
                }

                ReleaseCopy[] rows = asked.Recall(indexer.Name, term) ?? await AskAsync(indexer, term, question, about, asked, ct);
                ReleaseCopy[] titled = [.. rows.Where(row => question.Counts(row.Title))];
                ReleaseCopy[] named = [.. titled.Where(row => !question.UploadedBeforeItAired(row))];

                if (named.Length < titled.Length)
                {
                    journal.Noted(
                        ActivityStage.Find,
                        about,
                        $"{indexer.Name} · {titled.Length - named.Length} results uploaded more than a week before it aired were left out: another programme of the same name");
                }

                if (named.Length > 0)
                {
                    counted.AddRange(named.Select(row => (question.NameOf(row), row)));

                    break;
                }
            }
        }

        return [.. counted];
    }

    /// <summary>Asks once, reads a hash for every row that counts and carries none, and keeps the answer for the run.</summary>
    private async Task<ReleaseCopy[]> AskAsync(
        SourceDefinition indexer,
        SearchTerm term,
        Question question,
        string about,
        AskedThisCycle asked,
        CancellationToken ct)
    {
        ReleaseCopy[] rows = await find.AskAsync(indexer, term, about, ct, asked);

        for (int at = 0; at < rows.Length; at++)
        {
            // Only a row that counts, date included: a page read for a row that was uploaded years before the
            // episode is a request for a torrent that is never taken.
            if (rows[at].InfoHash is null && question.Takes(rows[at]))
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
