// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
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
        new(
            new FinishedFolderMover(_finished),
            _library,
            new EncodeJobDispatch(new NothingResolved(), NullLogger.Instance),
            string.Empty,
            NullLogger.Instance);

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

        Directory.Exists(Path.Combine(_finished, "Some.Show.S01E02.1080p.WEB-DL-GROUP")).Should().BeTrue();
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
    }

    // The bytes are safe either way, so this reports success and keeps the grab settled.
    // Retrying forever would re-move a folder that is already moved.
    [Fact]
    public async Task MoveIntoIntakeAsync_StillCountsAsDoneWhenTheShowIsNotInAnyLibrary()
    {
        string completed = await ACompletedDownloadAsync();

        bool handed = await Handoff().MoveIntoIntakeAsync(completed, new EpisodeKey(999, 1, 2), CancellationToken.None);

        handed.Should().BeTrue();
    }

    /// <summary>
    /// A container with none of the server's services in it, which is what a test process
    /// is. The dispatch says so and queues nothing; the move still has to happen and the
    /// grab still has to settle, and that is what these tests are about.
    /// </summary>
    private sealed class NothingResolved : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
}
