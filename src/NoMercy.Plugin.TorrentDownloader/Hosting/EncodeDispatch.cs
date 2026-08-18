using System.Collections;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Handing a staged episode to the server's own encoder.
/// </summary>
/// <remarks>
/// <para>
/// The plugin does not import into the library. It stages the finished video
/// and dispatches the same job the dashboard's <em>Add content</em> button
/// does, because <c>FileRescanJob</c> only re-walks folders the library already
/// knows and cannot see a file staged elsewhere.
/// </para>
/// <para>
/// Everything here is reached by name through <see cref="IServiceProvider"/> and
/// never by reference. Referencing the encoder and the entity model would make
/// them part of this plugin's ABI, which is the whole thing the plugin contract
/// exists to avoid — every trap below is from docs/09-host-contract.md and
/// every one of them was measured against the real server.
/// </para>
/// </remarks>
public sealed class EncodeDispatch(IServiceProvider services, IActivityJournal journal, ILogger logger)
{
    /// <summary>What queues the job.</summary>
    public const string DispatcherType = "NoMercy.MediaProcessing.Jobs.IJobDispatcher";

    /// <summary>The job itself.</summary>
    public const string JobType = "NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob";

    /// <summary>
    /// The library repository.
    /// </summary>
    /// <remarks>
    /// This name is ambiguous and the ambiguity is fatal:
    /// <c>NoMercy.MediaProcessing.Libraries.ILibraryRepository</c> shares it and
    /// is <strong>not registered</strong>. Asking for that one resolves to
    /// nothing and every encode is refused with "library X was not found" about
    /// a library that is right there. It is spelled in full for that reason and
    /// there is no short-name fallback, because a short name cannot choose.
    /// </remarks>
    public const string LibrariesType = "NoMercy.Data.Repositories.ILibraryRepository";

    /// <summary>What knows the server's own id for a file.</summary>
    public const string FilesType = "NoMercy.MediaProcessing.Files.IFileListService";

    /// <summary>
    /// Queues an encode for one staged file, and says whether it went.
    /// </summary>
    /// <remarks>
    /// Nothing here throws. An encode that cannot be queued is one download left
    /// staged and a line in the log naming exactly what could not be found: it
    /// used to throw out of a reflection call and unwind the whole transfers
    /// cadence, so one type mismatch stopped every download in flight from being
    /// looked at.
    /// </remarks>
    /// <param name="stagedFile">The video, in the intake folder.</param>
    /// <param name="libraryId">
    /// The show's own library, from <c>PluginLibraryShow.LibraryId</c>. That is
    /// what puts an anime episode in the anime library and a television episode
    /// in the tv library: the server decided the media type when the show was
    /// filed, and this plugin follows it rather than choosing.
    /// </param>
    /// <param name="libraryType">The library's type, as the file list service wants it.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<bool> DispatchAsync(string stagedFile, string libraryId, string libraryType, CancellationToken ct)
    {
        try
        {
            if (Named(DispatcherType) is not Type dispatcherType
                || Named(JobType) is not Type jobType
                || Named(LibrariesType) is not Type librariesType
                || Named(FilesType) is not Type filesType)
            {
                return Refused($"the server does not have {Missing()}");
            }

            // Inside a scope. The repository is scoped because it opens a
            // database context, and a scoped service asked of the root provider
            // is an exception or a null — cadences are not requests and have no
            // scope of their own, so one is made here.
            using IServiceScope scope = services.CreateScope();

            if (scope.ServiceProvider.GetService(librariesType) is not object libraries
                || scope.ServiceProvider.GetService(filesType) is not object files
                || scope.ServiceProvider.GetService(dispatcherType) is not object dispatcher)
            {
                return Refused("the server did not hand over the encoder's own services");
            }

            // The full one, never the Lite variant: Lite includes nothing, so
            // the library comes back folderless and the encode is refused for
            // having nowhere to go — on a library with two folders.
            object? library = await Awaited(Call(libraries, "GetLibraryByIdAsync", libraryId)).ConfigureAwait(false);

            if (library is null)
            {
                return Refused($"library {libraryId} was not found");
            }

            // The first folder, with no preference between them. Preferring one
            // whose path is non-empty is wrong: a real library's second folder
            // is a drive whose location lives on its storage driver.
            if (First(Read(library, "FolderLibraries")) is not object folder)
            {
                return Refused($"library {libraryId} has no folder to put anything in");
            }

            string full = Path.GetFullPath(stagedFile);

            // From the server, never from the filename. A job whose id matches
            // no media is dropped in silence: the queue counter moves and the
            // encode never runs.
            if (MatchId(files, Path.GetDirectoryName(full)!, libraryType, full) is not string id)
            {
                logger.LogWarning("The server matched nothing to {File}, so no encode was dispatched.", full);
                journal.Failed(ActivityStage.Download, Path.GetFileName(full), "the server matched no media to this file");

                return false;
            }

            object job = Activator.CreateInstance(jobType)
                         ?? throw new InvalidOperationException("the encode job could not be constructed");

            Write(job, "LibraryId", libraryId);
            Write(job, "FolderId", Read(folder, "FolderId"));
            Write(job, "Id", id);
            Write(job, "InputFile", full);

            // SourceDriverId is left unset: a finished download is on this
            // machine. PresetId comes from the library, and null there keeps
            // the folder's own presets.
            Write(job, "PresetId", Read(library, "EncodePresetId"));

            Call(dispatcher, "Dispatch", job, Read(job, "QueueName"), Read(job, "Priority"));

            journal.Finished(ActivityStage.Download, Path.GetFileName(full), $"encode dispatched to library {libraryId}");

            return true;
        }
        catch (Exception whatever)
        {
            // Every reflection call, every server type that has moved, every
            // null nobody expected. One download stays staged; nothing else
            // stops.
            return Refused(whatever.Message);
        }
    }

    /// <summary>A type by its full name, wherever the server loaded it from.</summary>
    private static Type? Named(string name)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(assembly => assembly.GetType(name, throwOnError: false))
            .FirstOrDefault(type => type is not null);
    }

    /// <summary>Which of the four is not there, for a message the owner can act on.</summary>
    private static string Missing()
    {
        return string.Join(
            ", ",
            new[] { DispatcherType, JobType, LibrariesType, FilesType }.Where(one => Named(one) is null));
    }

    /// <summary>The server's own id for this file, or null when it knows of none.</summary>
    private static string? MatchId(object files, string folder, string libraryType, string full)
    {
        // The two-argument overload. The three-argument one takes a storage
        // driver and is for a folder on a remote share.
        MethodInfo? walk = files.GetType()
            .GetMethods()
            .FirstOrDefault(one => one.Name == "GetFilesInDirectory" && one.GetParameters().Length == 2);

        if (walk is null || walk.Invoke(files, [folder, libraryType]) is not IEnumerable found)
        {
            return null;
        }

        foreach (object? item in found)
        {
            if (item is null || Read(item, "Path") is not string path)
            {
                continue;
            }

            if (Path.GetFullPath(path) == full && Read(item, "Match") is object match)
            {
                return Read(match, "Id")?.ToString();
            }
        }

        return null;
    }

    private bool Refused(string reason)
    {
        logger.LogWarning("No encode was dispatched: {Reason}.", reason);
        journal.Failed(ActivityStage.Download, "encode", reason);

        return false;
    }

    private static object? Call(object instance, string method, params object?[] arguments)
    {
        return instance.GetType().GetMethod(method)?.Invoke(instance, arguments);
    }

    private static async Task<object?> Awaited(object? returned)
    {
        if (returned is not Task waiting)
        {
            return returned;
        }

        await waiting.ConfigureAwait(false);

        return waiting.GetType().GetProperty("Result")?.GetValue(waiting);
    }

    private static object? Read(object instance, string property)
    {
        return instance.GetType().GetProperty(property)?.GetValue(instance);
    }

    private static void Write(object instance, string property, object? value)
    {
        instance.GetType().GetProperty(property)?.SetValue(instance, value);
    }

    private static object? First(object? items)
    {
        if (items is not IEnumerable list)
        {
            return null;
        }

        foreach (object? one in list)
        {
            return one;
        }

        return null;
    }
}
