using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

public class HostLibraryTests
{
    /// <remarks>
    /// <strong>C6.</strong> <c>GetShowsAsync(null)</c> returns every show in
    /// every library — it only filters when a library id is passed. An adapter
    /// that asks for everything at once would try to download episodes of
    /// films' shows and of libraries the owner never meant for this.
    /// </remarks>
    [Fact]
    public async Task OnlyTelevisionAndAnimeLibrariesAreRead()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Library("lib-anime", "Anime", "anime")
            .Library("lib-films", "Films", "movie")
            .Library("lib-music", "Music", "music")
            .Show(1, "Silo", "lib-tv")
            .Show(2, "Frieren", "lib-anime")
            .Show(3, "A film's show", "lib-films")
            .Show(4, "A concert", "lib-music");

        IReadOnlyList<Show> shows = await new HostLibrary(host).GetShowsAsync(CancellationToken.None);

        Assert.Equal(["Silo", "Frieren"], shows.Select(show => show.Title));
    }

    /// <remarks>
    /// The same rule as above, asserted on the request rather than the answer.
    /// Deliberately doubled: the test above only bites while the fake keeps
    /// behaving like the real server, and this one bites whatever the fake does.
    /// </remarks>
    [Fact]
    public async Task ShowsAreAskedForPerLibraryAndNeverAllAtOnce()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Library("lib-films", "Films", "movie");

        await new HostLibrary(host).GetShowsAsync(CancellationToken.None);

        Assert.DoesNotContain(null, host.Asked);
        Assert.Equal(["lib-tv"], host.Asked);
    }

    /// <remarks>
    /// The type comes from the library the server already filed the show in.
    /// The plugin classifies nothing: whether something is television or anime
    /// is the server's Kitsu-backed decision, and a second opinion here would
    /// be a second answer.
    /// </remarks>
    [Fact]
    public async Task AShowCarriesTheMediaTypeOfItsLibraryAndItsLibraryId()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Library("lib-anime", "Anime", "anime")
            .Show(1, "Silo", "lib-tv")
            .Show(2, "Frieren", "lib-anime");

        IReadOnlyList<Show> shows = await new HostLibrary(host).GetShowsAsync(CancellationToken.None);

        Show television = shows.Single(show => show.Title == "Silo");
        Show anime = shows.Single(show => show.Title == "Frieren");

        Assert.Equal(LibraryKind.Television, television.Kind);
        Assert.Equal(LibraryKind.Anime, anime.Kind);

        // Kept, because a downloaded episode goes back to the library its show
        // came from — never to one this plugin picked.
        Assert.Equal("lib-tv", television.LibraryId);
        Assert.Equal("lib-anime", anime.LibraryId);
    }

    /// <remarks>
    /// A library type nobody recognises is out of scope rather than a guess.
    /// <c>Library.Type</c> is a plain string column with no enum behind it, so
    /// an unknown value is a thing that can really arrive.
    /// </remarks>
    [Fact]
    public async Task ALibraryTypeThisPluginDoesNotKnowIsSkipped()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-odd", "Something else", "documentaries")
            .Show(1, "Whatever", "lib-odd");

        Assert.Empty(await new HostLibrary(host).GetShowsAsync(CancellationToken.None));
    }

    /// <remarks>
    /// The type is compared case-insensitively for the same reason: a plain
    /// string column holds whatever was written into it.
    /// </remarks>
    [Fact]
    public async Task TheLibraryTypeIsMatchedWhateverItsCase()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "TV")
            .Library("lib-anime", "Anime", "Anime")
            .Show(1, "Silo", "lib-tv")
            .Show(2, "Frieren", "lib-anime");

        Assert.Equal(2, (await new HostLibrary(host).GetShowsAsync(CancellationToken.None)).Count);
    }

    /// <remarks>
    /// No folder means nowhere to download to, so the show is not in scope.
    /// </remarks>
    [Fact]
    public async Task AShowWithNoFolderIsSkipped()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Show(1, "Silo", "lib-tv", folder: "Silo")
            .Show(2, "Homeless", "lib-tv", folder: null)
            .Show(3, "Blank", "lib-tv", folder: "   ");

        IReadOnlyList<Show> shows = await new HostLibrary(host).GetShowsAsync(CancellationToken.None);

        Assert.Equal(["Silo"], shows.Select(show => show.Title));
    }

    [Fact]
    public async Task TheYearComesThrough()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Show(1, "Sugar", "lib-tv", year: 2024)
            .Show(2, "Undated", "lib-tv");

        IReadOnlyList<Show> shows = await new HostLibrary(host).GetShowsAsync(CancellationToken.None);

        Assert.Equal(2024, shows.Single(show => show.Title == "Sugar").Year);
        Assert.Null(shows.Single(show => show.Title == "Undated").Year);
    }

    /// <remarks>
    /// <strong>C7.</strong> <c>HaveEpisodeCount</c> is the <c>Tv.HaveEpisodes</c>
    /// column, and on a real server it is nought for shows with hundreds of
    /// episodes on disk — a show with everything looks like a show with
    /// nothing. Presence comes from each episode's own <c>HasFile</c>, which is
    /// <c>episode.VideoFiles.Any()</c> and is correct.
    /// </remarks>
    [Fact]
    public async Task PresenceComesFromEachEpisodeNotFromTheShowsCount()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Show(1, "Silo", "lib-tv", episodeCount: 3, haveEpisodeCount: 0)
            .Episode(1, 1, 1, hasFile: true)
            .Episode(1, 1, 2, hasFile: true)
            .Episode(1, 1, 3, hasFile: false);

        IReadOnlyList<Episode> episodes = await new HostLibrary(host).GetEpisodesAsync(1, CancellationToken.None);

        Assert.Equal(2, episodes.Count(episode => episode.HasFile));
        Assert.Single(episodes, episode => !episode.HasFile);
    }

    /// <remarks>
    /// Neither count is on <see cref="Show"/> at all. Two numbers that can
    /// disagree must never both be trusted, and the surest way for the wrong
    /// one never to be read is for it not to be there to read.
    /// </remarks>
    [Fact]
    public void TheCountsThatCanLieAreNotOnTheDomainShowAtAll()
    {
        string[] properties = [.. typeof(Show).GetProperties().Select(property => property.Name)];

        Assert.DoesNotContain("HaveEpisodeCount", properties);
        Assert.DoesNotContain("EpisodeCount", properties);
    }

    /// <remarks>
    /// The air date decides whether a missing episode is one to look for or one
    /// still to come, so it has to survive the crossing intact. Null survives
    /// as null: an episode with no announced date is not an episode that aired
    /// at the epoch.
    /// </remarks>
    [Fact]
    public async Task TheAirDateComesThroughAndSoDoesItsAbsence()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-tv", "Shows", "tv")
            .Show(1, "Silo", "lib-tv")
            .Episode(1, 2, 3, "Aired", new DateTime(2026, 5, 17, 22, 30, 0, DateTimeKind.Utc))
            .Episode(1, 2, 4, "Unannounced", null);

        IReadOnlyList<Episode> episodes = await new HostLibrary(host).GetEpisodesAsync(1, CancellationToken.None);

        Assert.Equal(new DateOnly(2026, 5, 17), episodes[0].AirDate);
        Assert.Null(episodes[1].AirDate);
        Assert.Equal(new EpisodeKey(1, 2, 3), episodes[0].Key);
    }

    /// <remarks>
    /// Films are out of scope and the call is never made. The fake throws if it
    /// is, so this is not a matter of nobody having noticed.
    /// </remarks>
    [Fact]
    public async Task FilmsAreNeverAskedFor()
    {
        FakeLibraryQuery host = new FakeLibraryQuery()
            .Library("lib-films", "Films", "movie")
            .Library("lib-tv", "Shows", "tv")
            .Show(1, "Silo", "lib-tv");

        await new HostLibrary(host).GetShowsAsync(CancellationToken.None);
    }
}
