using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
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
    /// <summary>The library every test asks for unless it says otherwise.</summary>
    private const string Wanted = "01KZGKX2G0966V80H26EKGG5T0";

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
        Assert.Equal(FakeLibraries.FirstFolder, job.FolderId);
        Assert.Equal(FakeLibraries.Preset, job.PresetId);

        // A finished download is on this machine.
        Assert.Null(job.SourceDriverId);

        // The three-argument overload of Dispatch.
        Assert.Equal("encoder", server.Dispatcher.Queue);
        Assert.Equal(4, server.Dispatcher.Priority);
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

                // The database among them: the episode's own id is read from
                // the server's own table, and a context asked of the root
                // provider is the same fault as a repository asked of it.
                "NoMercy.Database.MediaContext",
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
    /// <para>
    /// <strong>The folder this show already lives in.</strong> A library can
    /// have several, on different drives — the owner's has two — and taking the
    /// first sent every encode to a drive the server could not reach: every job
    /// failed with "could not find a part of the path".
    /// </para>
    /// <para>
    /// The dashboard's own Add content does not guess either; it sends the
    /// folder the person browsing chose.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEncodeGoesToTheFolderTheShowIsAlreadyIn()
    {
        FakeProvider server = Server();

        // Two folders, and the show's episodes are in the second.
        server.Libraries.Library = new(
            FakeLibraries.Preset,
            [
                new(FakeLibraries.FirstFolder, @"Y:\nomercy\media"),
                new(FakeLibraries.SecondFolder, @"Z:\nomercy\TV.Shows"),
            ]);

        Assert.True(await Dispatch(
            server,
            "tv",
            existing: @"Z:\nomercy\TV.Shows\Silo.(2023)\Silo.S03E01.mkv"));

        Assert.Equal(
            FakeLibraries.SecondFolder,
            Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).FolderId);
    }

    /// <remarks>
    /// The library is the show's own, which is what puts an anime episode in
    /// the anime library and a television one in the tv library. The server
    /// decided the media type when the show was filed and this plugin follows
    /// it rather than choosing.
    /// </remarks>
    [Theory]
    [InlineData("01KZGKX2G0966V80H26EKGG5T0", "tv")]
    [InlineData("01KZGKX2G0966V80H26EKGG5A0", "anime")]
    public async Task TheEpisodeGoesToTheShowsOwnLibrary(string libraryId, string libraryType)
    {
        FakeProvider server = Server();

        Assert.True(await Dispatch(server, libraryType, libraryId));

        // As a Ulid, which is what the server's job carries. The plugin's own
        // contract spells every id as text, so something has to convert it —
        // and until 23 August 2026 nothing did, so writing the string threw and
        // every encode was refused.
        Assert.Equal(Ulid.Parse(libraryId), Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).LibraryId);
        Assert.Equal(Ulid.Parse(libraryId), server.Libraries.Asked);
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
            FakeLibraries.Preset,
            [new(FakeLibraries.FirstFolder, string.Empty), new(FakeLibraries.SecondFolder, "D:\\tv")]);

        await Dispatch(server, "tv");

        Assert.Equal(FakeLibraries.FirstFolder, Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).FolderId);
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
    /// <para>
    /// <strong>The same thing the dashboard's own Add content does.</strong> It
    /// builds a VideoEncodeJob out of the id the file listing gave it and
    /// dispatches it — and where that id is empty it dispatches anyway:
    /// <c>if (claim.Key.Length == 0) { selected.AddRange(claim); continue; }</c>
    /// </para>
    /// <para>
    /// This refused instead, so a file the owner could add by hand was one the
    /// plugin would not. Being stricter than the server's own interface is not
    /// a safety: it is the plugin deciding it knows better about a call it does
    /// not own.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AFileTheServerMatchedNothingToIsStillDispatched()
    {
        FakeProvider server = Server();

        // Listed, with no media matched to it.
        server.Files.Matches = [(Staged(), "0")];

        Assert.True(await Dispatch(server, "tv"));

        VideoEncodeJob job = Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job);

        Assert.Equal(Path.GetFullPath(Staged()), job.InputFile);

        // And it is said, because a job the encoder can do nothing with is not
        // something to discover from a library that stays empty.
        Assert.Contains(
            server.Log.Lines,
            one => one.Contains("matched no media", StringComparison.Ordinal));
    }

    /// <remarks>
    /// <para>
    /// <strong>Which of the two it is.</strong> The server skips a file its own
    /// parser cannot read a title out of, so it never appears in the listing at
    /// all — and a file that is missing from the listing is a different problem
    /// from a file that is listed and could not be identified. One is a name
    /// this plugin chose, the other is a show the server does not know.
    /// </para>
    /// <para>
    /// Both used to say "the server matched nothing to this file", which named
    /// the file and explained neither.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ItSaysWhetherTheFileWasListedAtAll()
    {
        FakeProvider missing = Server();

        // The folder has a file in it, and it is not ours.
        missing.Files.Matches = [(Path.Combine(_folder, "Something.Else.mkv"), "9")];

        Assert.False(await Dispatch(missing, "tv"));
        Assert.Contains(missing.Log.Lines, one => one.Contains("not among them", StringComparison.Ordinal));

        // Ours listed but matched to nothing is a third case and is dispatched
        // anyway, as the dashboard does — see
        // AFileTheServerMatchedNothingToIsStillDispatched.
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
        Assert.Contains(noLibrary.Log.Lines, one => one.Contains(Wanted, StringComparison.Ordinal));

        FakeProvider angry = Server();

        angry.Libraries.Throw = true;

        Assert.False(await Dispatch(angry, "tv"));
        Assert.NotEmpty(angry.Journal.Snapshot().History);

        FakeProvider folderless = Server();

        folderless.Libraries.Library = new(FakeLibraries.Preset, []);

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

    /// <remarks>
    /// <para>
    /// <strong>The plugin knows which episode this is; the server was left to
    /// guess.</strong> The encode job carries a media id, and the job does
    /// <c>Id.ToInt()</c> with it to find the row to register the encode
    /// against. An empty id becomes 0, matches nothing, and the job ends with
    /// "Post-encode registration found 0 files" — the queue counter moves and
    /// the library stays empty.
    /// </para>
    /// <para>
    /// That id used to come from asking the server to identify the file all
    /// over again from its name, a text search against a catalogue. On the
    /// owner's server on 24 August 2026 that came back empty for every episode
    /// the plugin staged, while the row it wanted — Sugar S02E08, 6900394 —
    /// was sitting in the server's own table the whole time.
    /// </para>
    /// <para>
    /// So it is looked up by what the plugin already knows for certain: the
    /// show, the season and the number it went and downloaded.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheEpisodesOwnIdIsLookedUpRatherThanGuessedFromTheName()
    {
        FakeProvider server = Server();

        // The server lists the file and matches it to nothing, which is what it
        // did for every episode the owner staged.
        server.Files.Matches = [(Staged(), "0")];

        server.Media = new()
        {
            Episodes = new NoMercy.Database.Models.TvShows.Episode[]
            {
                new() { Id = 6900393, TvId = 203744, SeasonNumber = 2, EpisodeNumber = 7 },
                new() { Id = 6900394, TvId = 203744, SeasonNumber = 2, EpisodeNumber = 8 },
                new() { Id = 5551234, TvId = 60625, SeasonNumber = 2, EpisodeNumber = 8 },
            }.AsQueryable(),
        };

        Assert.True(await Dispatch(server, "tv", episode: new(203744, 2, 8)), string.Join(" | ", server.Log.Lines));

        Assert.Equal("6900394", Assert.IsType<VideoEncodeJob>(server.Dispatcher.Job).Id);
    }

    private Task<bool> Dispatch(
        FakeProvider server,
        string libraryType,
        string libraryId = Wanted,
        string? existing = null,
        EpisodeKey? episode = null)
    {
        return new EncodeDispatch(server, server.Journal, server.Log).DispatchAsync(
            Staged(),
            libraryId,
            libraryType,
            existing,
            episode ?? new(203744, 2, 8),
            CancellationToken.None);
    }

    private FakeProvider Server()
    {
        FakeProvider server = new();

        server.Files.Matches = [(Staged(), "4417")];

        return server;
    }
}
