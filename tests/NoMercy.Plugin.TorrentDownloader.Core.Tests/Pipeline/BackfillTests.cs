using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Sources;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

using Xunit;

using static NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport.NameSourceSites;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

/// <summary>
/// An episode no feed named, looked up in the search of every name source.
/// </summary>
/// <remarks>
/// <c>docs/specs/release-names.md</c> § Backfill and <c>run.md</c>: for an episode, the release names are
/// the feed release names taken for it, and an episode with none is looked up by show and episode in
/// every name source's search. Every search answers with what it really answered on 15 September 2026.
/// </remarks>
public sealed class BackfillTests
{
    /// <remarks>
    /// Silo S02E01 aired in 2024 and no feed of the day carries it. All four searches are asked, and the
    /// names of all four come back — SceneSource's own spelling among them, which no other source has.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeNoFeedNamedIsLookedUpInEverySourcesSearch()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnsweringSiloSearches();
        NameSources sources = Over(fetch);
        // On the day it really aired: a name is judged against that date, and SceneSource's was published
        // the same day.
        TrackedEpisode silo = Episode(41, "Silo", 2, 1, year: 2023) with { AirDate = new DateOnly(2024, 11, 15) };

        FeedNamesTaken taken = await sources.ReadFeedsAsync([silo], CancellationToken.None);

        Assert.Empty(taken.For(silo.Key));

        IReadOnlyList<SourceName> names = await sources.NamesForAsync(silo, taken, CancellationToken.None);

        Assert.Equal(
            SiloSearches.Order(StringComparer.Ordinal),
            fetch.Asked.Select(address => address.ToString()).Where(address => !Feeds.Contains(address)).Order(StringComparer.Ordinal));

        Assert.Equal(
            ["PreDB", "PreDB.net", "SceneSource", "srrDB search"],
            names.Select(name => name.Source).Distinct().Order(StringComparer.Ordinal));

        Assert.Contains(names, name => name.Title == "Silo S02E01 The Engineer 1080p ATVP WEB-DL DDP5 1 Atmos H264-FLUX");
        Assert.Contains(names, name => name.Title == "Silo.S02E01.1080p.WEB.H264-SuccessfulCrab" && name.Source == "srrDB search");
    }

    /// <remarks>
    /// An episode a feed named is worked on with those names, and no source's search is asked about it:
    /// a search is a paced request, and this one has no use.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeAFeedNamedIsNotLookedUp()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnsweringSiloSearches();
        NameSources sources = Over(fetch);
        TrackedEpisode allAmerican = Episode(2, "All American", 8, 11, year: 2018);

        FeedNamesTaken taken = await sources.ReadFeedsAsync([allAmerican], CancellationToken.None);
        IReadOnlyList<SourceName> names = await sources.NamesForAsync(allAmerican, taken, CancellationToken.None);

        Assert.Equal(["All American S08E11 1080p WEB H264-GGWP"], names.Select(name => name.Title));
        Assert.All(fetch.Asked, address => Assert.Contains(address.ToString(), Feeds));
    }

    /// <remarks>
    /// <c>release-names.md</c>: looked up by show and episode, and the plugin builds no search term of its
    /// own beyond that. No quality, no year for a one-word title, no absolute number for an anime — each
    /// search is asked exactly <c>Show SxxEyy</c>, once, in the query style its source declares.
    /// </remarks>
    [Fact]
    public async Task TheSearchAsksShowAndEpisodeAndNothingElse()
    {
        FakeFetch fetch = new FakeFetch().AnsweringFeeds().AnswersAnything(Capture.Fixture("names-srrdb-search-silo-s02e01.json"));
        NameSources sources = Over(fetch);

        TrackedEpisode silo = Episode(41, "Silo", 2, 1, year: 2023);
        TrackedEpisode frieren = Episode(7, "Frieren", 1, 13, year: 2023, kind: LibraryKind.Anime, absolute: 13);

        Dictionary<TrackedEpisode, IReadOnlyList<SourceName>> answered = [];

        foreach (TrackedEpisode episode in (TrackedEpisode[])[silo, frieren])
        {
            FeedNamesTaken taken = await sources.ReadFeedsAsync([episode], CancellationToken.None);

            answered[episode] = await sources.NamesForAsync(episode, taken, CancellationToken.None);
        }

        // Every search answered Frieren with srrDB's page of Silo names: a search answers with whatever it
        // thinks is near, and only what names the episode asked about is kept.
        Assert.NotEmpty(answered[silo]);
        Assert.Empty(answered[frieren]);

        (string Show, string Episode)[] askedAbout = [("Silo", "S02E01"), ("Frieren", "S01E13")];

        string[] expected =
        [
            .. askedAbout
                .SelectMany(asked => Shipped
                    .Where(source => source.Role.HasFlag(SourceRole.Names))
                    .Select(source => Query.Write(source.SearchAddress!, $"{asked.Show} {asked.Episode}", source.Query))),
        ];

        Assert.Equal(
            expected.Order(StringComparer.Ordinal),
            fetch.Asked.Select(address => address.ToString()).Where(address => !Feeds.Contains(address)).Order(StringComparer.Ordinal));
    }
}
