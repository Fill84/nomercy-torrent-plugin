using Microsoft.Extensions.Time.Testing;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Pipeline;
using NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.Pipeline;

public class MissingRefreshTests
{
    private static readonly DateTimeOffset Today = new(2026, 8, 14, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Aired = new(2026, 1, 1);

    /// <remarks>
    /// <c>docs/specs/show-list.md</c> § Switching a show on and § Library preferences: a show is searched
    /// for only when it is switched on, its settings are saved, and it has a quality of its own or from
    /// its library. Off with everything saved, on with no quality anywhere, and never touched are all
    /// searched for not at all.
    /// </remarks>
    [Fact]
    public async Task OnlyShowsSwitchedOnWithAQualityAreSearchedFor()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: Aired)
            .Show(2, "Severance")
            .Episode(2, 1, 1, airDate: Aired)
            .Show(3, "Andor")
            .Episode(3, 1, 1, airDate: Aired)
            .Show(4, "Lioness")
            .Episode(4, 1, 1, airDate: Aired)
            .Show(5, "Frieren", 2023, LibraryKind.Anime, "lib-anime", "Anime")
            .Episode(5, 1, 1, airDate: Aired);

        FakeAppliedSettings settings = new FakeAppliedSettings()
            .Library(new("lib-anime") { Quality = "1080p" })

            // Its own quality.
            .Show(new(1) { SwitchedOn = true, Quality = "2160p" })

            // On and saved, and a quality neither on the show nor on its library.
            .Show(new(2) { SwitchedOn = true })

            // Everything saved, and switched off.
            .Show(new(3) { SwitchedOn = false, Quality = "720p" })

            // Lioness is never touched. Frieren follows its library's quality.
            .Show(new(5) { SwitchedOn = true });

        Assert.Equal(["Silo", "Frieren"], (await Derive(library, settings)).Select(episode => episode.ShowTitle));
    }

    /// <remarks>
    /// The overview's row button switches a show on, and that is all a show needs: its library's settings
    /// apply until the owner changes the show's own (the owner's rule of 16 September 2026).
    /// </remarks>
    [Fact]
    public async Task AShowSwitchedOnFromItsRowIsSearchedWithItsLibrarysSettings()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, airDate: Aired);

        FakeAppliedSettings settings = new FakeAppliedSettings()
            .Library(new("lib-tv") { Quality = "1080p" })
            .Show(new(1) { SwitchedOn = true });

        Assert.Single(await Derive(library, settings));
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: whether a show has a video file on disk plays no part. Until
    /// 15 September 2026 a show with nothing on disk was taken to be a row the server keeps that nobody
    /// added (<c>Ownership</c>, after the 479 grabs of 24 August). The switch is the owner saying which
    /// shows they want, so a show they have just added and switched on is searched from its first
    /// episode.
    /// </remarks>
    [Fact]
    public async Task AShowWithNothingOnDiskIsSearchedForOnceSwitchedOn()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "The Simpsons")
            .Episode(1, 1, 1, airDate: Aired)
            .Episode(1, 1, 2, airDate: Aired);

        FakeAppliedSettings settings = new FakeAppliedSettings()
            .Show(new(1) { SwitchedOn = true, Quality = "720p" });

        Assert.Equal(2, (await Derive(library, settings)).Count);
    }

    /// <remarks>
    /// <c>docs/specs/show-list.md</c>: with specials off no episode of season 0 is searched for, and a
    /// show's specials are its library's until the show sets its own — either way round.
    /// </remarks>
    [Fact]
    public async Task SeasonZeroIsSearchedOnlyWithSpecialsOnForThatShow()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 0, 1, airDate: Aired)
            .Episode(1, 1, 1, airDate: Aired)
            .Show(2, "Severance")
            .Episode(2, 0, 1, airDate: Aired)
            .Episode(2, 1, 1, airDate: Aired)
            .Show(3, "Frieren", 2023, LibraryKind.Anime, "lib-anime", "Anime")
            .Episode(3, 0, 1, airDate: Aired)
            .Episode(3, 1, 1, airDate: Aired)
            .Show(4, "Dandadan", 2024, LibraryKind.Anime, "lib-anime", "Anime")
            .Episode(4, 0, 1, airDate: Aired)
            .Episode(4, 1, 1, airDate: Aired);

        FakeAppliedSettings settings = new FakeAppliedSettings()
            .Library(new("lib-tv") { Quality = "1080p", Specials = false })
            .Library(new("lib-anime") { Quality = "1080p", Specials = true })

            // On for itself, in a library with specials off.
            .Show(new(1) { SwitchedOn = true, Specials = true })

            // Following its library: off.
            .Show(new(2) { SwitchedOn = true })

            // Off for itself, in a library with specials on.
            .Show(new(3) { SwitchedOn = true, Specials = false })

            // Following its library: on.
            .Show(new(4) { SwitchedOn = true });

        Assert.Equal(
            [new EpisodeKey(1, 0, 1), new EpisodeKey(4, 0, 1)],
            (await Derive(library, settings)).Where(episode => episode.Key.IsSpecial).Select(episode => episode.Key));

        Assert.Equal(4, (await Derive(library, settings)).Count(episode => !episode.Key.IsSpecial));
    }

    /// <remarks>
    /// An episode that is on disk is not a gap. Every other episode of the same
    /// show is, and that is the whole of the rule now.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeAlreadyOnDiskIsNotTracked()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 1, hasFile: true, airDate: new DateOnly(2020, 1, 1))
            .Episode(1, 1, 2, airDate: new DateOnly(2020, 1, 8));

        IReadOnlyList<TrackedEpisode> tracked = await Derive(library);

        TrackedEpisode only = Assert.Single(tracked);

        Assert.Equal(2, only.Key.Number);
    }

    /// <remarks>
    /// Looking for an episode that has not aired finds either nothing or
    /// something that should not exist yet, and both are worse than waiting.
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWithNoAirDateOrOneStillToComeIsNotAired()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "Silo")
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
    /// Everything a page needs to name the episode travels with it, so the
    /// queue can be drawn without asking the library again.
    /// </remarks>
    [Fact]
    public async Task WhatAPageNeedsToNameTheEpisodeTravelsWithIt()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(7, "Sugar", 2024, LibraryKind.Anime)
            .Episode(7, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
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
            .Episode(1, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
            .Episode(2, 1, 99, airDate: new DateOnly(2015, 1, 1), hasFile: true)
            .Episode(1, 1, 1, airDate: new DateOnly(2026, 1, 1))
            .Episode(2, 1, 1, airDate: new DateOnly(2026, 1, 1));

        await Derive(library);

        Assert.Equal([1, 2], library.EpisodesAskedFor);
    }

    /// <remarks>
    /// <para>
    /// <strong>An episode whose file is in the library under its own name is not missing</strong>, whatever
    /// row the server attached that file to. South Park S15E12's encode is in
    /// <c>/South.Park.(1997)/South.Park.S15E12/</c>, registered against season 0 (media-server #38), so its
    /// own row shows no file. It was searched for, downloaded again and handed to an encoder that skipped it,
    /// because every output was already there.
    /// </para>
    /// <para>
    /// The file's own name is the same proof <see cref="Landed"/> reads once an encode is over.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEpisodeWhoseFileTheServerFiledUnderAnotherIsNotMissing()
    {
        FakeLibrary library = new FakeLibrary()
            .Show(1, "South Park", 1997)
            .Episode(1, 0, 12, airDate: Aired, hasFile: true)
            .Episode(1, 15, 12, airDate: Aired)
            .Episode(1, 15, 13, airDate: Aired);

        library.Files[1] = ["/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8"];

        Assert.Equal([new EpisodeKey(1, 15, 13)], (await Derive(library)).Select(episode => episode.Key));
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

    private static async Task<IReadOnlyList<TrackedEpisode>> Derive(FakeLibrary library, FakeAppliedSettings? settings = null)
    {
        FakeTimeProvider clock = new(Today);

        return await new MissingRefresh(library, settings ?? FakeAppliedSettings.EveryShowOn(), clock)
            .DeriveAsync(CancellationToken.None);
    }
}
