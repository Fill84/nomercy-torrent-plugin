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
    /// An episode the harvest already found a name for costs nothing at all.
    /// The feeds are read once a quarter of an hour and the search runs every
    /// six; on a library where the pool is doing its job, most episodes never
    /// reach a name database.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeAnsweredByThePoolCostsNoRequests()
    {
        FakePool pool = new();
        await pool.AddAsync(
            [new(PoolKey.For("Silo", 3, 6), "Silo.S03E06.1080p.WEB.H264-CAKES", "PreDB", When)],
            CancellationToken.None);

        FakeFetch fetch = new();

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, pool)
            .ResolveAsync([Episode("Silo", 3, 6)], CancellationToken.None);

        Assert.Empty(fetch.Asked);

        ResolvedNames only = Assert.Single(resolved);
        Assert.Equal("Silo.S03E06.1080p.WEB.H264-CAKES", Assert.Single(only.Titles));
    }

    /// <remarks>
    /// A miss asks, and asks once for the season rather than once for the
    /// episode. Two episodes of one season are one question: the answer to
    /// "what is Silo season three called" covers both of them, and asking twice
    /// is a request a site did not need to serve.
    /// </remarks>
    [Fact]
    public async Task TwoEpisodesOfOneSeasonCostOneQueryPerSource()
    {
        FakeFetch fetch = new();
        fetch.AnswersAnything(Capture.Fixture("srrdb-search.json"));

        await Resolving(fetch).ResolveAsync(
            [Episode("Silo", 3, 6), Episode("Silo", 3, 7)],
            CancellationToken.None);

        // One per name database, and there are two of them.
        Assert.Equal(2, fetch.Asked.Count);
        Assert.Equal(
            ["api.srrdb.com", "predb.me"],
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

        await Resolving(fetch).ResolveAsync(episodes, CancellationToken.None);

        Assert.Equal(6, fetch.Asked.Count(address => address.Host == "api.srrdb.com"));
        Assert.Equal(6, fetch.Asked.Count(address => address.Host == "predb.me"));
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
            CancellationToken.None);

        string[] asked = [.. fetch.Asked.Where(address => address.Host == "api.srrdb.com").Select(Term)];

        Assert.Equal(["sugar-2024-s01", "sugar-s01"], asked.Order());
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
            CancellationToken.None);

        Assert.Equal(
            ["monsters-of-god-s01"],
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
            CancellationToken.None);

        Assert.Equal(
            ["sousou-no-frieren", "sousou-no-frieren-s01"],
            fetch.Asked.Where(address => address.Host == "api.srrdb.com").Select(Term).Order());
    }

    /// <remarks>
    /// An anime episode is looked up under both of its numbers. The harvest
    /// files an absolute-numbered release under the number it carries — this
    /// one is a real row off the Nyaa capture — and an episode looked up only
    /// under its season would ask a name database for something already in
    /// hand.
    /// </remarks>
    [Fact]
    public async Task AnAnimeEpisodeIsFoundUnderItsAbsoluteNumber()
    {
        FakePool pool = new();
        await pool.AddAsync(
            [new(PoolKey.ForAbsolute("One Piece", 1172), "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv", "Nyaa", When)],
            CancellationToken.None);

        FakeFetch fetch = new();

        IReadOnlyList<ResolvedNames> resolved = await Resolving(fetch, pool).ResolveAsync(
            [Episode("One Piece", 21, 45, kind: LibraryKind.Anime, absolute: 1172)],
            CancellationToken.None);

        Assert.Empty(fetch.Asked);
        Assert.Equal(
            "[KiyoshiiSubs] One Piece - 1172v2 [1080p][H.265 - 10Bit].mkv",
            Assert.Single(Assert.Single(resolved).Titles));
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
            .ResolveAsync([Episode("Silo", 3, 6)], CancellationToken.None);

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
            .ResolveAsync([Episode("Silo", 3, 6)], CancellationToken.None);

        Assert.Empty(Assert.Single(resolved).Titles);

        Assert.Contains(
            journal.Snapshot().History,
            entry => entry.Stage == ActivityStage.Names
                     && entry.Outcome == ActivityOutcome.Finished
                     && entry.Subject == "Silo S03");
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
            .ResolveAsync([Episode("Silo", 3, 6)], CancellationToken.None);

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
