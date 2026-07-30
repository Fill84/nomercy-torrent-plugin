// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using NoMercy.Plugin.TorrentDownloader.Adapters;
using NoMercy.Plugin.TorrentDownloader.Core.Library;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Adapters;

public class PluginLibraryQueryAdapterTests
{
    [Fact]
    public async Task GetShowsAsync_ReturnsShowsFromEveryTvLibrary()
    {
        FakeLibraryQuery fake = new()
        {
            Libraries = [new PluginLibrary("lib-tv", "TV Shows", "tv"), new PluginLibrary("lib-movie", "Movies", "movie")],
            ShowsByLibraryId = new Dictionary<string, List<PluginLibraryShow>>
            {
                ["lib-tv"] = [new PluginLibraryShow(1, "Show One", 2020, "lib-tv", "/shows/one", 10, 5)],
            },
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryShow> shows = await adapter.GetShowsAsync(CancellationToken.None);

        shows.Should().ContainSingle();
        shows[0].ShowId.Should().Be(1);
    }

    [Fact]
    public async Task GetShowsAsync_IncludesAnimeLibraries()
    {
        FakeLibraryQuery fake = new()
        {
            Libraries = [new PluginLibrary("lib-anime", "Anime", "anime")],
            ShowsByLibraryId = new Dictionary<string, List<PluginLibraryShow>>
            {
                ["lib-anime"] = [new PluginLibraryShow(2, "Anime One", 2019, "lib-anime", "/anime/one", 12, 12)],
            },
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryShow> shows = await adapter.GetShowsAsync(CancellationToken.None);

        shows.Should().ContainSingle();
        shows[0].ShowId.Should().Be(2);
    }

    [Fact]
    public async Task GetShowsAsync_ExcludesMusicAndMovieLibraries()
    {
        FakeLibraryQuery fake = new()
        {
            Libraries =
            [
                new PluginLibrary("lib-music", "Music", "music"),
                new PluginLibrary("lib-movie", "Movies", "movie"),
            ],
            ShowsByLibraryId = new Dictionary<string, List<PluginLibraryShow>>
            {
                ["lib-music"] = [new PluginLibraryShow(3, "Not A Show", null, "lib-music", null, 0, 0)],
                ["lib-movie"] = [new PluginLibraryShow(4, "Also Not A Show", null, "lib-movie", null, 0, 0)],
            },
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryShow> shows = await adapter.GetShowsAsync(CancellationToken.None);

        shows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetShowsAsync_MapsEveryFieldIncludingANullFolder()
    {
        PluginLibraryShow source = new(5, "Show Five", 2021, "lib-tv", null, 20, 8);
        FakeLibraryQuery fake = new()
        {
            Libraries = [new PluginLibrary("lib-tv", "TV Shows", "tv")],
            ShowsByLibraryId = new Dictionary<string, List<PluginLibraryShow>> { ["lib-tv"] = [source] },
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryShow> shows = await adapter.GetShowsAsync(CancellationToken.None);

        LibraryShow mapped = shows.Should().ContainSingle().Which;
        mapped.ShowId.Should().Be(source.Id);
        mapped.Title.Should().Be(source.Title);
        mapped.Year.Should().Be(source.Year);
        mapped.LibraryId.Should().Be(source.LibraryId);
        mapped.Folder.Should().BeNull();
        mapped.EpisodeCount.Should().Be(source.EpisodeCount);
        mapped.HaveEpisodeCount.Should().Be(source.HaveEpisodeCount);
    }

    [Fact]
    public async Task GetEpisodesAsync_KeepsEpisodesWithNoFile()
    {
        FakeLibraryQuery fake = new()
        {
            Episodes = [new PluginLibraryEpisode(1, 1, 1, "Pilot", null, false)],
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryEpisode> episodes = await adapter.GetEpisodesAsync(1, CancellationToken.None);

        LibraryEpisode episode = episodes.Should().ContainSingle().Which;
        episode.HasFile.Should().BeFalse();
    }

    [Fact]
    public async Task GetEpisodesAsync_TreatsAnUnspecifiedAirDateAsUtc()
    {
        DateTime unspecified = new(2026, 7, 22, 0, 0, 0, DateTimeKind.Unspecified);
        FakeLibraryQuery fake = new()
        {
            Episodes = [new PluginLibraryEpisode(1, 1, 1, "Pilot", unspecified, true)],
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryEpisode> episodes = await adapter.GetEpisodesAsync(1, CancellationToken.None);

        DateTimeOffset? airDate = episodes.Should().ContainSingle().Which.AirDate;
        airDate.Should().NotBeNull();
        airDate!.Value.Offset.Should().Be(TimeSpan.Zero);
        airDate.Value.Day.Should().Be(22);
        airDate.Value.Month.Should().Be(7);
        airDate.Value.Year.Should().Be(2026);
    }

    [Fact]
    public async Task GetEpisodesAsync_MapsANullAirDateToNull()
    {
        FakeLibraryQuery fake = new()
        {
            Episodes = [new PluginLibraryEpisode(1, 1, 1, "Pilot", null, true)],
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryEpisode> episodes = await adapter.GetEpisodesAsync(1, CancellationToken.None);

        episodes.Should().ContainSingle().Which.AirDate.Should().BeNull();
    }

    [Fact]
    public async Task GetFilesAsync_MapsPathAndQuality()
    {
        PluginLibraryFile source = new(1, 1, 1, "/shows/one/s01e01.mkv", "1080p");
        FakeLibraryQuery fake = new() { Files = [source] };
        PluginLibraryQueryAdapter adapter = new(fake);

        IReadOnlyList<LibraryFile> files = await adapter.GetFilesAsync(1, CancellationToken.None);

        LibraryFile mapped = files.Should().ContainSingle().Which;
        mapped.Path.Should().Be(source.Path);
        mapped.Quality.Should().Be(source.Quality);
    }

    [Fact]
    public async Task GetShowsAsync_AsksForLibrariesOnceAndShowsOncePerTvLibrary()
    {
        FakeLibraryQuery fake = new()
        {
            Libraries =
            [
                new PluginLibrary("lib-tv-1", "TV One", "tv"),
                new PluginLibrary("lib-tv-2", "TV Two", "tv"),
                new PluginLibrary("lib-tv-3", "TV Three", "tv"),
            ],
            ShowsByLibraryId = new Dictionary<string, List<PluginLibraryShow>>
            {
                ["lib-tv-1"] = [new PluginLibraryShow(1, "One", 2020, "lib-tv-1", "/one", 1, 1)],
                ["lib-tv-2"] = [new PluginLibraryShow(2, "Two", 2020, "lib-tv-2", "/two", 1, 1)],
                ["lib-tv-3"] = [new PluginLibraryShow(3, "Three", 2020, "lib-tv-3", "/three", 1, 1)],
            },
        };
        PluginLibraryQueryAdapter adapter = new(fake);

        await adapter.GetShowsAsync(CancellationToken.None);

        fake.GetLibrariesCallCount.Should().Be(1);
        fake.GetShowsCallCount.Should().Be(3);
    }
}
