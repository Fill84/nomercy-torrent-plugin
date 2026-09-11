using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Sources.Readers;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// Working out what an episode's release is called.
/// </summary>
/// <remarks>
/// The pool first, and the name databases only for what it cannot answer. Every
/// request this stage does not make is one a site does not have to tolerate,
/// and the whole shape of the stage is about not making them.
/// </remarks>
public class NameResolveTests
{
    /// <remarks>
    /// <para>
    /// <strong>The owner's rule of 11 September 2026: the sources are asked
    /// about every missing episode on every run.</strong> The pool only adds to
    /// what they answer; it never stands in for them.
    /// </para>
    /// <para>
    /// It used to stand in. An episode with any name at all in the pool was
    /// never asked about again — and on the owner's server Dark Matter S02E03
    /// had one, <c>Dark.Matter.S02E03.MULTi.1080p.WEB.H264-AZR</c> from 1
    /// September, which English only refuses. So no source was asked, and no
    /// name the owner could take ever reached an indexer.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithANameInThePoolIsStillAskedAboutEveryRun()
    {
        FakePool pool = new();
        await pool.AddAsync(
            [new(PoolKey.For("Silo", 3, 6), "Silo.S03E06.MULTi.1080p.WEB.H264-AZR", "PreDB", When)],
            CancellationToken.None);

        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        NameResolve resolving = Resolving(fetch, pool);
        TrackedEpisode silo = Episode("Silo", 3, 6);

        IReadOnlyList<string> names = await resolving.NamesForAsync(
            silo,
            await resolving.FromPoolAsync([silo], CancellationToken.None),
            Wanted,
            CancellationToken.None);

        // Asked, although the pool already had a name for it.
        Assert.Contains(fetch.Asked, address => address.Host == "api.srrdb.com");

        // And what the pool held is still one of the candidates.
        Assert.Contains("Silo.S03E06.MULTi.1080p.WEB.H264-AZR", names);
    }

    /// <remarks>
    /// <para>
    /// <strong>Each episode is asked about on its own.</strong> It used to be
    /// one question per season — two episodes of one season were one request —
    /// and that saving is what hid the releases the owner wanted: a source
    /// asked about a season answers with the season, and the episode's own
    /// releases fall past the cut.
    /// </para>
    /// <para>
    /// So two episodes are two questions per source, and each of those may
    /// climb a rung when the first finds nothing. The gate paces it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EachEpisodeIsAskedAboutOnItsOwn()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Silo", 3, 6), Episode("Silo", 3, 7)],
            Wanted,
            CancellationToken.None);

        // Two episodes, two sources. srrDB answers the first question for both
        // of them; predb.me cannot read that answer and climbs a rung, so it
        // asks twice each.
        Assert.Equal(
            ["api.srrdb.com", "api.srrdb.com", "predb.me", "predb.me", "predb.me", "predb.me"],
            fetch.Asked.Select(address => address.Host).Order());
    }

    /// <remarks>
    /// Six seasons, forty-two episodes, six questions per name database — and
    /// forty-two would be a plugin that gets itself rate-limited on the first
    /// library it meets.
    /// </remarks>
    [Fact]
    public async Task FortyTwoEpisodesAcrossSixSeasonsCostSixQueriesPerSource()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        TrackedEpisode[] episodes =
        [
            .. Enumerable.Range(1, 6).SelectMany(season =>
                Enumerable.Range(1, 7).Select(number => Episode("Silo", season, number))),
        ];

        Assert.Equal(42, episodes.Length);

        await Resolving(fetch).ResolveAsync(episodes, Wanted, CancellationToken.None);

        // One question per episode now, not one per season. Six seasons of seven
        // episodes is forty-two questions rather than six — the price of asking
        // the question a source can actually answer.
        Assert.Equal(42, fetch.Asked.Count(address => address.Host == "api.srrdb.com"));
    }

    /// <remarks>
    /// A show whose title is a common word is asked both ways and the answers
    /// are pooled together. Four shows in the real library need it — Lucky,
    /// Sugar, Lioness and Silo — and searching <em>Sugar</em> alone answers
    /// with a documentary about beekeeping.
    /// </remarks>
    [Fact]
    public async Task AShowNeedingItsYearIsAskedUnderBothForms()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Sugar", 1, 1, year: 2024)],
            Wanted,
            CancellationToken.None);

        string[] asked = [.. fetch.Asked.Where(address => address.Host == "api.srrdb.com").Select(Term)];

        // The episode with the quality, then without, then the same two under
        // the year. srrDB answers the first, so only that one is asked.
        Assert.Equal(["sugar-s01e01-1080p"], asked.Order());
    }

    /// <remarks>
    /// A show with more than one word in its title is asked once. The year is
    /// what makes a one-word title searchable, and adding it to every show
    /// doubles every request for nothing.
    /// </remarks>
    [Fact]
    public async Task AShowWhoseTitleIsNotOneWordIsAskedOnce()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Monsters of God", 1, 2, year: 2026)],
            Wanted,
            CancellationToken.None);

        // A title of more than one word is never asked under its year, so the
        // episode's own question is the only one this source needs.
        Assert.Equal(
            ["monsters-of-god-s01e02-1080p"],
            fetch.Asked.Where(address => address.Host == "api.srrdb.com").Select(Term));
    }

    /// <remarks>
    /// Anime is posted under both forms and neither can be guessed from the
    /// other, so both are asked. An absolute-numbered release carries no season
    /// tag at all, which is why the second question has none either.
    /// </remarks>
    [Fact]
    public async Task AnAnimeShowIsAskedUnderTheSeasonalAndTheAbsoluteForm()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Sousou no Frieren", 1, 13, kind: LibraryKind.Anime, absolute: 13)],
            Wanted,
            CancellationToken.None);

        // srrDB answers the first question, so the absolute form is not needed
        // here. That it exists at all is the next test, which asks a source
        // that answers nothing.
        Assert.Equal(
            ["sousou-no-frieren-s01e13-1080p"],
            fetch.Asked.Where(address => address.Host == "api.srrdb.com").Select(Term).Order());
    }

    /// <remarks>
    /// An anime episode is looked up under both of its numbers. The harvest
    /// files an absolute-numbered release under the number it carries — this
    /// one is a real row off the Nyaa capture — and an episode looked up only
    /// under its season would never see it. The sources are asked as well, as
    /// they are for every episode on every run; what the pool holds is added
    /// to what they answer.
    /// </remarks>
    [Fact]
    public async Task AnAnimeEpisodeIsFoundUnderItsAbsoluteNumber()
    {
        FakePool pool = new();
        await pool.AddAsync(
            [new(PoolKey.ForAbsolute("One Piece", 1172), "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv", "Nyaa", When)],
            CancellationToken.None);

        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, pool).ResolveAsync(
            [Episode("One Piece", 21, 45, kind: LibraryKind.Anime, absolute: 1172)],
            Wanted,
            CancellationToken.None);

        Assert.Contains(
            "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv",
            Assert.Single(resolved).Titles);
    }

    /// <remarks>
    /// A season pack in the pool is a candidate for every episode of that
    /// season — the harvest files one under its season and nothing else would
    /// ever look there, so without this the pack rules could not be reached at
    /// all. It is not an <em>answer</em>, though: whether a pack is worth
    /// taking depends on how many gaps the season has, and an episode whose
    /// only candidate is a pack still asks the name databases for a release of
    /// its own.
    /// </remarks>
    [Fact]
    public async Task ASeasonPackIsACandidateForEveryEpisodeOfThatSeasonAndNeverAnAnswer()
    {
        FakePool pool = new();
        await pool.AddAsync(
            [new(PoolKey.ForSeason("Silo", 3), "Silo.S03.1080p.WEB.H264-CAKES", "PreDB", When)],
            CancellationToken.None);

        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, pool)
            .ResolveAsync([Episode("Silo", 3, 6)], Wanted, CancellationToken.None);

        Assert.Contains("Silo.S03.1080p.WEB.H264-CAKES", Assert.Single(resolved).Titles);

        // And it was still asked, because a pack is not an answer.
        Assert.NotEmpty(fetch.Asked);
    }

    /// <remarks>
    /// What comes back is pooled, so the next episode of that season — and the
    /// next cycle — costs nothing. A stage that asked and threw the answer away
    /// would ask again every six hours for ever.
    /// </remarks>
    [Fact]
    public async Task WhatTheNameDatabasesAnswerIsPooled()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        FakePool pool = new();

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, pool)
            .ResolveAsync([Episode("Silo", 3, 6)], Wanted, CancellationToken.None);

        Assert.Contains("Silo.S03E06.1080p.WEB.H264-CAKES", Assert.Single(resolved).Titles);

        Assert.Contains(
            pool.Names,
            name => name.Key == PoolKey.For("Silo", 3, 6)
                    && name.Title == "Silo.S03E06.1080p.WEB.H264-CAKES");
    }

    /// <remarks>
    /// An episode nothing has a name for is answered honestly with none, and
    /// the stage says so. It is not an error: srrDB answering zero for a show
    /// with no scene releases is an answer, and the episode is asked about
    /// again next cycle.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeNothingHasANameForResolvesToNone()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        ActivityJournal journal = new();

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, journal: journal)
            .ResolveAsync([Episode("Silo", 3, 6)], Wanted, CancellationToken.None);

        Assert.Empty(Assert.Single(resolved).Titles);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Names
                     && entry.Outcome == ActivityOutcome.Finished
                     && entry.Subject == "Silo S03E06");
    }

    /// <remarks>
    /// One name database being down is one name database being down. The other
    /// still answers, and the episode is not lost because a site was.
    /// </remarks>
    [Fact]
    public async Task OneNameDatabaseThatFailsDoesNotTakeTheOthersWithIt()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));
        fetch.FailsHost("predb.me", FetchOutcome.Unreachable, "predb.me did not answer");

        ActivityJournal journal = new();

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, journal: journal)
            .ResolveAsync([Episode("Silo", 3, 6)], Wanted, CancellationToken.None);

        Assert.NotEmpty(Assert.Single(resolved).Titles);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Outcome == ActivityOutcome.Failed
                     && entry.Detail!.Contains("did not answer", StringComparison.Ordinal));
    }

    private static readonly DateTimeOffset When = new(2026, 8, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>The term inside a srrDB address, which is its last path segment.</summary>
    private static string Term(Uri address)
    {
        return address.Segments[^1];
    }

    /// <remarks>
    /// <para>
    /// <strong>A source is asked about the episode, with the owner's quality,
    /// and nothing broader.</strong> The owner's rule of 11 September 2026, and
    /// it is measured. srrDB asked <c>south-park-s15</c> — the season, which is
    /// what this stage used to ask — answers 286 releases and hands back the
    /// first 45: DVD rips, a making-of documentary, and not one release of the
    /// episode. Asked <c>south-park-s15e12-1080p</c> it answers four, one of
    /// which is <c>South.Park.S15E12.1080p.BluRay.x264-FilmHD</c>.
    /// </para>
    /// <para>
    /// So the release the owner wanted existed at the source the whole time,
    /// and the plugin asked a question that buried it. The pool held four names
    /// for that episode, all German, 2160p or XviD, and the cycle fell through
    /// to guessing at the indexers.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASourceIsAskedAboutTheEpisodeWithTheOwnersQuality()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Silo", 3, 6)],
            new() { MaximumResolution = "1080p" },
            CancellationToken.None);

        // The first question put to each source names the quality. A source that
        // answers nothing to it is asked again without, which is the rung below
        // and the next test.
        foreach (IGrouping<string, Uri> site in fetch.Asked.GroupBy(address => address.Host))
        {
            Assert.Contains("1080p", site.First().ToString(), StringComparison.OrdinalIgnoreCase);
        }

        Assert.All(
            fetch.Asked,
            address => Assert.Contains("s03e06", address.ToString(), StringComparison.OrdinalIgnoreCase));
    }

    /// <remarks>
    /// And where that finds nothing, the same question without the quality —
    /// the owner's ladder, at the source this time. A release nobody published
    /// in 1080p is still a release, and the profile refuses it one step later
    /// where every spelling is understood.
    /// </remarks>
    [Fact]
    public async Task ASourceThatAnswersNothingIsAskedTheEpisodeWithoutTheQuality()
    {
        FakeFetch fetch = new();

        // Nothing at all for the question that names the quality.
        fetch.AnswersAnything(Capture.Fixture("nyaa-nothing.xml"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Silo", 3, 6)],
            new() { MaximumResolution = "1080p" },
            CancellationToken.None);

        string[] asked = [.. fetch.Asked.Select(address => address.ToString())];

        Assert.Contains(asked, address => address.Contains("1080p", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(
            asked,
            address => address.Contains("s03e06", StringComparison.OrdinalIgnoreCase)
                && !address.Contains("1080p", StringComparison.OrdinalIgnoreCase));

        // And never the season on its own, which is what buried the release.
        Assert.DoesNotContain(
            asked,
            address => address.Contains("s03", StringComparison.OrdinalIgnoreCase)
                && !address.Contains("s03e06", StringComparison.OrdinalIgnoreCase));
    }

    private static TrackedEpisode Episode(
        string show,
        int season,
        int number,
        int? year = null,
        LibraryKind kind = LibraryKind.Television,
        int? absolute = null)
    {
        return new(
            new(show.GetHashCode(StringComparison.Ordinal), season, number),
            show,
            year,
            kind,
            null,
            new DateOnly(2026, 8, 1),
            EpisodeState.Missing,
            absolute);
    }

    /// <summary>The two name databases, as the catalogue really has them.</summary>
    private static readonly SourceDefinition[] NameDatabases =
    [
        new("srrDB search", "srrdb", "https://api.srrdb.com/v1/search/{query}") { Query = QueryStyles.Slug },
        new("PreDB", "rss", "https://predb.me/?rss=1")
        {
            SearchUrl = "https://predb.me/?search={query}&rss=1",
            SearchGated = true,
        },
    ];

    /// <summary>What the owner wants, at its documented defaults.</summary>
    private static Profile Wanted => new() { MaximumResolution = "1080p" };

    private static NameResolve Resolving(
        FakeFetch fetch,
        FakePool? pool = null,
        ActivityJournal? journal = null)
    {
        return new(
            SourceCatalogue.Build(NameDatabases, [], []),
            fetch,
            Readers.Shipped(),
            pool ?? new FakePool(),
            journal ?? new ActivityJournal(),
            TimeProvider.System);
    }
}
