// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using NoMercy.Plugins.Abstractions;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// The step that decides whether a finished download becomes an episode you can watch or
/// a file sitting in a folder nobody looks at.
/// </summary>
public class LibraryImportHandoffTests : IDisposable
{
    private const long BigEnough = 60 * 1024 * 1024;

    private static readonly Ulid LibraryUlid = Ulid.NewUlid();

    private readonly string _downloads = Directory.CreateTempSubdirectory("nm-tdl-dl-").FullName;
    private readonly string _finished = Directory.CreateTempSubdirectory("nm-tdl-fin-").FullName;
    private readonly RecordingEventBus _events = new();
    private readonly FakeLibraryQuery _library = new();

    public LibraryImportHandoffTests()
    {
        Add(LibraryUlid.ToString(), "Series", "tv", showId: 42, "Some Show");
    }

    public void Dispose()
    {
        foreach (string folder in new[] { _downloads, _finished })
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // A locked handle on a build agent is not a test failure.
            }
        }
    }

    private void Add(string libraryId, string title, string type, int showId, string showTitle)
    {
        _library.Libraries.Add(new PluginLibrary(libraryId, title, type));
        _library.ShowsByLibraryId[libraryId] =
        [
            new PluginLibraryShow(showId, showTitle, 2026, libraryId, $"/media/{showId}", 10, 1),
        ];
    }

    private LibraryImportHandoff Handoff() =>
        new(new FinishedFolderMover(_finished), _library, _events, NullLogger.Instance);

    private async Task<string> ACompletedDownloadAsync(string name = "Some.Show.S01E02.1080p.WEB-DL-GROUP")
    {
        string folder = Path.Combine(_downloads, name);
        Directory.CreateDirectory(folder);

        await using FileStream stream = File.Create(Path.Combine(folder, $"{name}.mkv"));
        stream.SetLength(BigEnough);

        return folder;
    }

    // Nothing watches the finished folder - it is not a library folder, and the server
    // only watches those. Publishing this event is the plugin saying "here, and it
    // belongs to that library"; without it the episode is complete on disk and invisible.
    [Fact]
    public async Task MoveIntoIntakeAsync_TellsTheServerWhereTheEpisodeLandedAndWhoseItIs()
    {
        string completed = await ACompletedDownloadAsync();

        bool handed = await Handoff().MoveIntoIntakeAsync(completed, new EpisodeKey(42, 1, 2), CancellationToken.None);

        handed.Should().BeTrue();

        FileCreatedEvent published = _events.Published.OfType<FileCreatedEvent>().Should().ContainSingle().Which;
        published.FolderPath.Should().Be(Path.Combine(_finished, "Some.Show.S01E02.1080p.WEB-DL-GROUP"));
        published.LibraryId.Should().Be(LibraryUlid);
        published.LibraryType.Should().Be("tv");
    }

    // The server's handler switches on the type and treats anime like tv but not like a
    // movie, so sending the show's own library type is what puts it through the right arm.
    [Fact]
    public async Task MoveIntoIntakeAsync_SendsTheLibrarysOwnTypeRatherThanAssumingTv()
    {
        Ulid animeLibrary = Ulid.NewUlid();
        Add(animeLibrary.ToString(), "Anime", "anime", showId: 7, "Some Anime");

        string completed = await ACompletedDownloadAsync("Some.Anime.S01E02.1080p");

        await Handoff().MoveIntoIntakeAsync(completed, new EpisodeKey(7, 1, 2), CancellationToken.None);

        _events.Published.OfType<FileCreatedEvent>().Single().LibraryType.Should().Be("anime");
    }

    [Fact]
    public async Task MoveIntoIntakeAsync_SaysNothingToTheServerWhenNothingMoved()
    {
        string empty = Path.Combine(_downloads, "no-video-here");
        Directory.CreateDirectory(empty);

        bool handed = await Handoff().MoveIntoIntakeAsync(empty, new EpisodeKey(42, 1, 2), CancellationToken.None);

        // Unfinished, so the next cycle tries again - and an import announced for a
        // folder with nothing in it would have the server scanning for a file that is
        // not there.
        handed.Should().BeFalse();
        _events.Published.Should().BeEmpty();
    }

    // The bytes are safe either way, so this reports success and keeps the grab settled.
    // Retrying forever would re-move a folder that is already moved.
    [Fact]
    public async Task MoveIntoIntakeAsync_StillCountsAsDoneWhenTheShowIsNotInAnyLibrary()
    {
        string completed = await ACompletedDownloadAsync();

        bool handed = await Handoff().MoveIntoIntakeAsync(completed, new EpisodeKey(999, 1, 2), CancellationToken.None);

        handed.Should().BeTrue();
        _events.Published.Should().BeEmpty();
    }

    private sealed class RecordingEventBus : IEventBus
    {
        public List<IEvent> Published { get; } = [];

        public Task PublishAsync<TEvent>(TEvent @event, CancellationToken ct = default)
            where TEvent : IEvent
        {
            Published.Add(@event);
            return Task.CompletedTask;
        }

        public IDisposable Subscribe<TEvent>(Func<TEvent, CancellationToken, Task> handler)
            where TEvent : IEvent => new Nothing();

        public IDisposable Subscribe<TEvent>(IEventHandler<TEvent> handler)
            where TEvent : IEvent => new Nothing();

        private sealed class Nothing : IDisposable
        {
            public void Dispose() { }
        }
    }
}
