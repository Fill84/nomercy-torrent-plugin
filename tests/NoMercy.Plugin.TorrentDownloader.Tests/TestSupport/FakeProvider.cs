using Microsoft.Extensions.DependencyInjection;
using NoMercy.Data.Repositories;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>A library, as the repository hands one over.</summary>
public sealed record FakeLibrary(Ulid? EncodePresetId, IReadOnlyList<FakeFolderLibrary> FolderLibraries);

/// <summary>
/// One folder of a library, in the server's own shape.
/// </summary>
/// <remarks>
/// The path hangs off a <c>Folder</c> and not off the link, because
/// <c>FolderLibrary.Folder.Path</c> is what the server has. Carrying the path
/// on the link itself made a test agree with a plugin that read the wrong
/// place, and every encode went to the library's first folder — a drive the
/// server could not reach.
/// </remarks>
public sealed record FakeFolderLibrary(Ulid FolderId, FakeFolder Folder)
{
    public FakeFolderLibrary(Ulid folderId, string path)
        : this(folderId, new FakeFolder(path))
    {
    }
}

public sealed record FakeFolder(string Path);

/// <summary>One file the server knows about, with its media match.</summary>
public sealed record FakeFile(string Path, FakeMatch Match);

public sealed record FakeMatch(int Id);

public sealed class FakeLibraries : ILibraryRepository
{
    /// <summary>The library's preset, a Ulid because the server's is one.</summary>
    public static Ulid Preset { get; } = Ulid.Parse("01KZGKX2G0966V80H26EKGG5JA");

    /// <summary>The folder an encode is expected to be sent to.</summary>
    public static Ulid FirstFolder { get; } = Ulid.Parse("01KZGKX2G0966V80H26EKGG5JB");

    public static Ulid SecondFolder { get; } = Ulid.Parse("01KZGKX2G0966V80H26EKGG5JC");

    public FakeLibrary? Library { get; set; } =
        new(Preset, [new(FirstFolder, "D:\\tv"), new(SecondFolder, "E:\\tv")]);

    public bool Throw { get; set; }

    public Ulid? Asked { get; private set; }

    public List<string> Called { get; } = [];

    public Task<object?> GetLibraryByIdAsync(Ulid id)
    {
        Called.Add(nameof(GetLibraryByIdAsync));
        Asked = id;

        return Throw
            ? throw new InvalidOperationException("the database went away")
            : Task.FromResult<object?>(Library);
    }

    /// <summary>
    /// The overload that makes the name ambiguous. Never called, and that is
    /// the point: it exists so that asking for the name alone throws here as it
    /// does on the real server.
    /// </summary>
    public Task<object?> GetLibraryByIdAsync(
        Ulid libraryId,
        Guid userId,
        string language,
        string country,
        int take,
        int page,
        CancellationToken ct = default)
    {
        Called.Add(nameof(GetLibraryByIdAsync) + " (the long one)");

        return Task.FromResult<object?>(Library);
    }

    public Task<object?> GetLibraryByIdLiteAsync(Ulid id, CancellationToken ct = default)
    {
        Called.Add(nameof(GetLibraryByIdLiteAsync));

        // Folderless, exactly as the real one is: a plugin that used this
        // would be refused for having nowhere to put the file.
        return Task.FromResult<object?>(new FakeLibrary(Preset, []));
    }
}

public sealed class FakeFiles : IFileListService
{
    public IReadOnlyList<(string Path, string Id)> Matches { get; set; } = [];

    public string? AskedType { get; private set; }

    public Task<List<object>> GetFilesInDirectory(string folder, string libraryType)
    {
        AskedType = libraryType;

        return Task.FromResult<List<object>>(
            [.. Matches.Select(one => (object)new FakeFile(one.Path, new(int.Parse(one.Id))))]);
    }

    public Task<List<object>> GetFilesInDirectory(string folder, string libraryType, object storage)
    {
        // The overload for a folder on a remote share. Reaching it from
        // here would mean the wrong one was chosen by parameter count.
        throw new InvalidOperationException("the three-argument overload is for a remote share");
    }
}

/// <summary>The server's own encoder, as the contract offers it.</summary>
/// <remarks>
/// It replaced a fake of <c>IJobDispatcher</c> and the job type that went with
/// it, which is what the plugin had to build for itself before media-server #30.
/// </remarks>
public sealed class FakeEncoder : IPluginEncoder
{
    private readonly List<(string File, string Library, string? Media, string? Preset)> _asked = [];
    private readonly List<(string File, string Library, string? Media, string? Preset)> _queued = [];

    /// <summary>Everything it was asked to encode, in order.</summary>
    public IReadOnlyList<(string File, string Library, string? Media, string? Preset)> Asked => _asked;

    /// <summary>Why it will not take it, or null to take it.</summary>
    public string? Refusal { get; set; }

    /// <summary>The last ask it took, or null where it took none.</summary>
    /// <remarks>
    /// Taken, not merely asked. A refusal is an ask that queued nothing, and a
    /// test that could not tell them apart would call a refused encode a
    /// dispatched one.
    /// </remarks>
    public (string File, string Library, string? Media, string? Preset)? Job =>
        _queued.Count == 0 ? null : _queued[^1];

    /// <summary>How many encodes it really queued.</summary>
    /// <remarks>
    /// Counted, not just kept: one release with eight grab rows dispatched
    /// eight identical jobs and the last one looked exactly like the first.
    /// </remarks>
    public int Dispatches => _queued.Count;

    public Task<PluginEncodeResult> EncodeAsync(
        string file,
        string libraryId,
        string? mediaId = null,
        string? presetId = null,
        CancellationToken ct = default)
    {
        _asked.Add((file, libraryId, mediaId, presetId));

        if (Refusal is not null)
        {
            return Task.FromResult(PluginEncodeResult.Refused(Refusal));
        }

        _queued.Add((file, libraryId, mediaId, presetId));

        return Task.FromResult(PluginEncodeResult.Queued("01KZGKX2G0966V80H26EKGG5T1"));
    }
}

public sealed class FakeDispatcher : IJobDispatcher
{
    public object? Job { get; private set; }

    public string? Queue { get; private set; }

    public int Priority { get; private set; }

    /// <summary>How many times anything was dispatched at all.</summary>
    /// <remarks>
    /// Counted, not just kept: one release with eight grab rows dispatched
    /// eight identical jobs and the last one looked exactly like the first.
    /// </remarks>
    public int Dispatches { get; private set; }

    public void Dispatch(object job, string queue, int priority)
    {
        Dispatches++;
        Job = job;
        Queue = queue;
        Priority = priority;
    }
}

/// <summary>The server, as far as this plugin can see it.</summary>
public sealed class FakeProvider : IServiceProvider, IServiceScopeFactory, IServiceScope
{
    public FakeLibraries Libraries { get; } = new();

    public FakeFiles Files { get; } = new();

    public FakeEncoder Encoder { get; } = new();

    public FakeDispatcher Dispatcher { get; } = new();

    /// <summary>The server's own database, holding the owner's episode rows.</summary>
    public NoMercy.Database.MediaContext Media { get; set; } = new();

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

        if (serviceType == typeof(NoMercy.Database.MediaContext))
        {
            return Media;
        }

        if (serviceType == typeof(IPluginEncoder))
        {
            return Encoder;
        }

        return serviceType == typeof(IJobDispatcher) ? Dispatcher : null;
    }

    public void Dispose()
    {
    }

    public sealed class Scoped(FakeProvider owner) : IServiceProvider
    {
        public object? GetService(Type serviceType)
        {
            owner.AskedInScope.Add(serviceType.FullName!);

            return owner.Resolve(serviceType);
        }
    }
}
