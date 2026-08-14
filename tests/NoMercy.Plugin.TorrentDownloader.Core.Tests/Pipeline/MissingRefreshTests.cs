using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

public class MissingRefreshTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    /// <remarks>
    /// Looking for an episode that has not aired finds either nothing or
    /// something that should not exist yet, and both are worse than waiting.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithNoAirDateOrOneStillToComeIsNotAired()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: null)
            .Episode(1, 1, 2, airDate: new DateOnly(2026, 9, 1))
            .Episode(1, 1, 3, airDate: new DateOnly(2026, 8, 15));

        IReadOnlyList<TrackedEpisode> tracked = await Derive(library);

        Assert.All(tracked, episode => Assert.Equal(EpisodeState.NotAired, episode.State));
    }

    /// <remarks>
    /// An episode airing today has aired. The library holds a broadcast day,
    /// not a moment, so "today is still to come" would hold an episode back for
    /// a whole extra cycle for no reason anyone could see.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeAiringTodayHasAired()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 8, 14));

        Assert.Equal(EpisodeState.Missing, (await Derive(library))[0].State);
    }

    /// <remarks>
    /// However old. There is no follow list, no subscription and no cut-off: an
    /// episode that aired two years ago counts exactly as much as last night's,
    /// because filling gaps backwards is the point of the plugin.
    /// </remarks>
    [Fact]
    public async Task AnAiredEpisodeWithNoFileIsMissingHoweverOld()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2019, 3, 2))
            .Episode(1, 5, 9, airDate: new DateOnly(2026, 8, 13));

        IReadOnlyList<TrackedEpisode> tracked = await Derive(library);

        Assert.Equal(2, tracked.Count);
        Assert.All(tracked, episode => Assert.Equal(EpisodeState.Missing, episode.State));
    }

    /// <remarks>
    /// <strong>B5.</strong> 0.3.4 refused to search a show whose status was not
    /// "still going", which looked like a sensible saving and was the exact
    /// opposite of backfill: an ended show is the kind with gaps to fill. There
    /// is no status to consult here and there is meant to be none.
    /// </remarks>
    [Fact]
    public async Task AShowThatEndedYearsAgoIsStillInScope()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Long finished", 2011)
            .Episode(1, 4, 7, airDate: new DateOnly(2014, 6, 1));

        Assert.Single(await Derive(library));
    }

    /// <remarks>
    /// An episode the library has a file for is not tracked at all. Presence is
    /// the absence of a row, so there is no second opinion about it to go stale.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeTheLibraryAlreadyHasIsNotTracked()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 1, 1), hasFile: true)
            .Episode(1, 1, 2, airDate: new DateOnly(2026, 1, 8), hasFile: false);

        IReadOnlyList<TrackedEpisode> tracked = await Derive(library);

        Assert.Equal(new EpisodeKey(1, 1, 2), Assert.Single(tracked).Key);
    }

    /// <remarks>
    /// Season 0 is specials, and the owner has to ask for them.
    /// </remarks>
    [Fact]
    public async Task SpecialsAreSkippedUnlessTheOwnerAskedForThem()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 0, 1, airDate: new DateOnly(2026, 1, 1))
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 1, 8));

        Assert.Single(await Derive(library));
        Assert.Equal(2, (await Derive(library, new() { IncludeSpecials = true })).Count);
    }

    /// <remarks>
    /// Everything a page needs to name the episode travels with it, so the
    /// queue can be drawn without asking the library again.
    /// </remarks>
    [Fact]
    public async Task WhatAPageNeedsToNameTheEpisodeTravelsWithIt()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(7, "Sugar", 2024, LibraryKind.Anime)
            .Episode(7, 2, 4, airDate: new DateOnly(2026, 2, 2), title: "The one with the cat");

        TrackedEpisode episode = Assert.Single(await Derive(library));

        Assert.Equal(new EpisodeKey(7, 2, 4), episode.Key);
        Assert.Equal("Sugar", episode.ShowTitle);
        Assert.Equal(2024, episode.ShowYear);
        Assert.Equal(LibraryKind.Anime, episode.Kind);
        Assert.Equal("The one with the cat", episode.EpisodeTitle);
        Assert.Equal(new DateOnly(2026, 2, 2), episode.AirDate);
    }

    /// <remarks>
    /// A derivation reads the library and nothing else. It cannot know how many
    /// times something has been searched for, and inventing a nought here would
    /// overwrite the count on every maintenance pass — see the repository,
    /// which is what keeps it.
    /// </remarks>
    [Fact]
    public async Task ADerivationKnowsNothingAboutAttempts()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 1, 1));

        TrackedEpisode episode = Assert.Single(await Derive(library));

        Assert.Equal(0, episode.Attempts);
        Assert.Null(episode.LastSearchAt);
    }

    /// <remarks>
    /// An anime episode carries the number its releases actually use. Without
    /// it the plugin can only search <c>S02E13</c>, and most of what exists is
    /// published as <c>- 37</c>.
    /// </remarks>
    [Fact]
    public async Task AnAnimeEpisodeCarriesItsAbsoluteNumber()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Frieren", 2023, LibraryKind.Anime)
            .Episode(1, 1, 24, airDate: new DateOnly(2024, 1, 1), hasFile: true)
            .Episode(1, 2, 13, airDate: new DateOnly(2026, 1, 1));

        // Twenty-four of season one, so season two's thirteenth is the
        // thirty-seventh of the series — and the twenty-third of those is
        // already on disk, which changes nothing about where it sits.
        TrackedEpisode episode = Assert.Single(await DeriveAnime(library, 24));

        Assert.Equal(37, episode.Absolute);
    }

    /// <remarks>
    /// Television has no absolute numbering, so a number here would be one no
    /// release anywhere uses — a search term guaranteed to find nothing, and a
    /// number on a page that means nothing.
    /// </remarks>
    [Fact]
    public async Task ATelevisionEpisodeHasNoAbsoluteNumber()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2024, 1, 1), hasFile: true)
            .Episode(1, 2, 3, airDate: new DateOnly(2026, 1, 1));

        Assert.Null(Assert.Single(await Derive(library)).Absolute);
    }

    /// <remarks>
    /// The map is built from the list the pipeline already fetched. Fetching
    /// again would be one extra call per show per cycle — invisible until a
    /// library with hundreds of shows made the maintenance pass take minutes.
    /// </remarks>
    [Fact]
    public async Task TheAbsoluteMapCostsNoExtraLibraryCall()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Frieren", 2023, LibraryKind.Anime)
            .Show(2, "Silo")
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 1, 1))
            .Episode(2, 1, 1, airDate: new DateOnly(2026, 1, 1));

        await Derive(library);

        Assert.Equal([1, 2], library.EpisodesAskedFor);
    }

    private static async Task<IReadOnlyList<TrackedEpisode>> DeriveAnime(FakeLibrary library, int seasonOneLength)
    {
        // Season one in full, so the offset is a real count rather than a
        // number written into the test.
        for (int number = 1; number < seasonOneLength; number++)
        {
            library.Episode(1, 1, number, airDate: new DateOnly(2024, 1, 1), hasFile: true);
        }

        return await Derive(library);
    }

    private static async Task<IReadOnlyList<TrackedEpisode>> Derive(FakeLibrary library, Profile? profile = null)
    {
        FakeTimeProvider clock = new(Today);

        return await new MissingRefresh(library, clock).DeriveAsync(profile ?? new Profile(), CancellationToken.None);
    }
}
