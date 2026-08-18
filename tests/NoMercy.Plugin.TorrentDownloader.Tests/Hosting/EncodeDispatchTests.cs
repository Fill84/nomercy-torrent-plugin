using Microsoft.Extensions.DependencyInjection;
using NoMercy.Data.Repositories;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Hosting;
using NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;
using Xunit;

namespace NoMercy.Plugin.TorrentDownloader.Tests.Hosting;

/// <summary>
/// Dispatching an encode, against a server made of the contract's own type names.
/// </summary>
/// <remarks>
/// <para>
/// Every trap asserted here is from docs/09-host-contract.md and every one was
/// measured against the real server. They are not hypothetical: each cost a
/// release.
/// </para>
/// <para>
/// The types in <c>FakeServer.cs</c> sit under the exact namespaces the contract
/// names, because the plugin reaches them by name and never by reference. That
/// is the only way this path can be tested without making the encoder part of
/// this plugin's ABI.
/// </para>
/// </remarks>
public class EncodeDispatchTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nomercy-dispatch-" + Guid.NewGuid().ToString("n")[..8]);

    /// <remarks>
    /// The whole path: the four types are asked for, inside a scope, and the
    /// job carries exactly what <c>ServerController.AddFiles</c> sets.
    /// </remarks>
    [Fact]
    public async Task TheJobCarriesTheServersOwnIdTheFullPathAndNoStorageDriver()
    {
        FakeProvider server = Server();

        Assert.True(await Dispatch(server, "tv"));

        VideoEncodeJob job = Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job);

        // The match's id, as a string. It was the int nought once, which threw
        // out of the reflection call, and an empty string once, which had the
        // job find no episode and return in silence while the queue counter
        // moved.
        Assert.Equal("4417", job.Id);

        Assert.Equal(Path.GetFullPath(Staged()), job.InputFile);
        Assert.Equal("folder-one", job.FolderId);
        Assert.Equal("preset-9", job.PresetId);

        // A finished download is on this machine.
        Assert.Null(job.SourceDriverId);

        // The three-argument overload of Dispatch.
        Assert.Equal("encoder", server.Dispatcher.Queue);
        Assert.Equal(5, server.Dispatcher.Priority);
    }

    /// <remarks>
    /// Inside a scope, because the repository is scoped: it opens a database
    /// context, and a scoped service asked of the root provider is an exception
    /// or a null. Cadences are not requests and have no scope of their own.
    /// </remarks>
    [Fact]
    public async Task EverythingIsResolvedInsideAScope()
    {
        FakeProvider server = Server();

        await Dispatch(server, "tv");

        Assert.Equal(1, server.Scopes);
        Assert.Empty(server.AskedOfTheRoot);

        Assert.Equal(
            [
                "NoMercy.Data.Repositories.ILibraryRepository",
                "NoMercy.MediaProcessing.Files.IFileListService",
                "NoMercy.MediaProcessing.Jobs.IJobDispatcher",
            ],
            server.AskedInScope.Order());
    }

    /// <remarks>
    /// The full one, not the Lite variant. Lite includes nothing, so the
    /// library comes back folderless and the encode is refused for having
    /// nowhere to go — on a library with two folders.
    /// </remarks>
    [Fact]
    public async Task TheLibraryIsFetchedWithTheVariantThatIncludesItsFolders()
    {
        FakeProvider server = Server();

        await Dispatch(server, "tv");

        Assert.Equal(["GetLibraryByIdAsync"], server.Libraries.Called);
    }

    /// <remarks>
    /// The library is the show's own, which is what puts an anime episode in
    /// the anime library and a television one in the tv library. The server
    /// decided the media type when the show was filed and this plugin follows
    /// it rather than choosing.
    /// </remarks>
    [Theory]
    [InlineData("library-tv", "tv")]
    [InlineData("library-anime", "anime")]
    public async Task TheEpisodeGoesToTheShowsOwnLibrary(string libraryId, string libraryType)
    {
        FakeProvider server = Server();

        Assert.True(await Dispatch(server, libraryType, libraryId));

        Assert.Equal(libraryId, Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).LibraryId);
        Assert.Equal(libraryId, server.Libraries.Asked);
        Assert.Equal(libraryType, server.Files.AskedType);
    }

    /// <remarks>
    /// The <em>first</em> folder, with no preference between them. Preferring
    /// one whose path is non-empty is wrong: a real library's second folder is
    /// a drive whose location lives on its storage driver, and the dialog lists
    /// it happily.
    /// </remarks>
    [Fact]
    public async Task TheFirstFolderIsUsedWhateverItsPathLooksLike()
    {
        FakeProvider server = Server();

        server.Libraries.Library = new(
            "preset-9",
            [new("folder-one", string.Empty), new("folder-two", "D:\\tv")]);

        await Dispatch(server, "tv");

        Assert.Equal("folder-one", Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).FolderId);
    }

    /// <remarks>
    /// Nothing matched, so nothing is dispatched: a job with no id is dropped
    /// in silence, which reports success and leaves the episode in a folder
    /// nobody is watching. The warning names the file.
    /// </remarks>
    [Fact]
    public async Task WhenTheServerMatchesNothingNothingIsDispatchedAndTheFileIsNamed()
    {
        FakeProvider server = Server();

        server.Files.Matches = [];

        Assert.False(await Dispatch(server, "tv"));

        Assert.Null(server.Dispatcher.Job);
        Assert.Contains(server.Log.Lines, one => one.Contains("Silo.S03E06.mkv", StringComparison.Ordinal));
        Assert.Contains(server.Journal.Snapshot().History, one => one.Outcome == ActivityOutcome.Failed);
    }

    /// <remarks>
    /// A file the server knows nothing about is not the file next to it in the
    /// same folder. Matching on anything looser than the full path would
    /// dispatch an encode for somebody else's episode.
    /// </remarks>
    [Fact]
    public async Task AMatchForAnotherFileInTheSameFolderIsNotThisFilesMatch()
    {
        FakeProvider server = Server();

        server.Files.Matches = [(Path.Combine(_folder, "Silo.S03E07.mkv"), "9999")];

        Assert.False(await Dispatch(server, "tv"));
        Assert.Null(server.Dispatcher.Job);
    }

    /// <remarks>
    /// <para>
    /// Nothing in this path throws. It used to throw out of a reflection call
    /// and unwind the whole transfers cadence, so one type mismatch stopped
    /// every download in flight from being looked at.
    /// </para>
    /// <para>
    /// A missing type logs what could not be found and returns false; a library
    /// that is not there does the same; a repository that throws does too.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task NothingThrowsWhateverTheServerDoes()
    {
        FakeProvider missing = Server();

        missing.Withhold = "NoMercy.MediaProcessing.Jobs.IJobDispatcher";

        Assert.False(await Dispatch(missing, "tv"));
        Assert.NotEmpty(missing.Log.Lines);

        FakeProvider noLibrary = Server();

        noLibrary.Libraries.Library = null;

        Assert.False(await Dispatch(noLibrary, "tv"));
        Assert.Contains(noLibrary.Log.Lines, one => one.Contains("library-tv", StringComparison.Ordinal));

        FakeProvider angry = Server();

        angry.Libraries.Throw = true;

        Assert.False(await Dispatch(angry, "tv"));
        Assert.NotEmpty(angry.Journal.Snapshot().History);

        FakeProvider folderless = Server();

        folderless.Libraries.Library = new("preset-9", []);

        Assert.False(await Dispatch(folderless, "tv"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private string Staged()
    {
        Directory.CreateDirectory(_folder);

        string path = Path.Combine(_folder, "Silo.S03E06.mkv");

        if (!File.Exists(path))
        {
            File.WriteAllBytes(path, new byte[16]);
        }

        return path;
    }

    private Task<bool> Dispatch(FakeProvider server, string libraryType, string libraryId = "library-tv")
    {
        return new EncodeDispatch(server, server.Journal, server.Log)
            .DispatchAsync(Staged(), libraryId, libraryType, CancellationToken.None);
    }

    private FakeProvider Server()
    {
        FakeProvider server = new();

        server.Files.Matches = [(Staged(), "4417")];

        return server;
    }

    /// <summary>A library, as the repository hands one over.</summary>
    private sealed record FakeLibrary(string? EncodePresetId, IReadOnlyList<FakeFolderLibrary> FolderLibraries);

    private sealed record FakeFolderLibrary(string FolderId, string Path);

    /// <summary>One file the server knows about, with its media match.</summary>
    private sealed record FakeFile(string Path, FakeMatch Match);

    private sealed record FakeMatch(int Id);

    private sealed class FakeLibraries : ILibraryRepository
    {
        public FakeLibrary? Library { get; set; } =
            new("preset-9", [new("folder-one", "D:\\tv"), new("folder-two", "E:\\tv")]);

        public bool Throw { get; set; }

        public string? Asked { get; private set; }

        public List<string> Called { get; } = [];

        public Task<object?> GetLibraryByIdAsync(string libraryId)
        {
            Called.Add(nameof(GetLibraryByIdAsync));
            Asked = libraryId;

            return Throw
                ? throw new InvalidOperationException("the database went away")
                : Task.FromResult<object?>(Library);
        }

        public Task<object?> GetLibraryByIdLiteAsync(string libraryId)
        {
            Called.Add(nameof(GetLibraryByIdLiteAsync));

            // Folderless, exactly as the real one is: a plugin that used this
            // would be refused for having nowhere to put the file.
            return Task.FromResult<object?>(new FakeLibrary("preset-9", []));
        }
    }

    private sealed class FakeFiles : IFileListService
    {
        public IReadOnlyList<(string Path, string Id)> Matches { get; set; } = [];

        public string? AskedType { get; private set; }

        public IEnumerable<object> GetFilesInDirectory(string folder, string libraryType)
        {
            AskedType = libraryType;

            return Matches.Select(one => (object)new FakeFile(one.Path, new(int.Parse(one.Id))));
        }

        public IEnumerable<object> GetFilesInDirectory(string folder, string libraryType, string storageDriverId)
        {
            // The overload for a folder on a remote share. Reaching it from
            // here would mean the wrong one was chosen by parameter count.
            throw new InvalidOperationException("the three-argument overload is for a remote share");
        }
    }

    private sealed class FakeDispatcher : IJobDispatcher
    {
        public object? Job { get; private set; }

        public string? Queue { get; private set; }

        public int Priority { get; private set; }

        public void Dispatch(object job, string queue, int priority)
        {
            Job = job;
            Queue = queue;
            Priority = priority;
        }
    }

    /// <summary>The server, as far as this plugin can see it.</summary>
    private sealed class FakeProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
    {
        public FakeLibraries Libraries { get; } = new();

        public FakeFiles Files { get; } = new();

        public FakeDispatcher Dispatcher { get; } = new();

        public ActivityJournal Journal { get; } = new();

        public CapturingLogger Log { get; } = new();

        /// <summary>A type the server has not registered.</summary>
        public string? Withhold { get; set; }

        public int Scopes { get; private set; }

        public List<string> AskedInScope { get; } = [];

        public List<string> AskedOfTheRoot { get; } = [];

        public IServiceProvider ServiceProvider => new Scoped(this);

        public IServiceScope CreateScope()
        {
            Scopes++;

            return this;
        }

        public object? GetService(Type serviceType)
        {
            if (serviceType == typeof(IServiceScopeFactory))
            {
                return this;
            }

            // Anything else asked of the root rather than of a scope is the
            // fault this test is watching for.
            AskedOfTheRoot.Add(serviceType.FullName!);

            return Resolve(serviceType);
        }

        public object? Resolve(Type serviceType)
        {
            if (serviceType.FullName == Withhold)
            {
                return null;
            }

            if (serviceType == typeof(ILibraryRepository))
            {
                return Libraries;
            }

            if (serviceType == typeof(IFileListService))
            {
                return Files;
            }

            return serviceType == typeof(IJobDispatcher) ? Dispatcher : null;
        }

        public void Dispose()
        {
        }

        private sealed class Scoped(FakeProvider owner) : IServiceProvider
        {
            public object? GetService(Type serviceType)
            {
                owner.AskedInScope.Add(serviceType.FullName!);

                return owner.Resolve(serviceType);
            }
        }
    }
}
