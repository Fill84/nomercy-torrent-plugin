// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Adding a finished download the way the dashboard's "Add content" adds one.
///
/// <para>
/// That button posts to <c>dashboard/server/addfiles</c>, and the controller behind it
/// builds a <c>VideoEncodeJob</c> and dispatches it. Nothing else happens: no watcher, no
/// scan, no event. The server's own comment says why, and it is the reason every other
/// route fails here - "FileRescanJob only re-walks existing library folders and cannot
/// see a file staged elsewhere". A finished download is staged elsewhere by definition.
/// </para>
///
/// <para>
/// So this does the same call. It reaches the host's services by name rather than by
/// reference: <c>IJobDispatcher</c> and the job live in the server's own assemblies, and
/// referencing those would make the encoder and the EF model part of this plugin's ABI -
/// the exact thing the plugin contract was shaped to avoid. The plugin runs inside the
/// server's process, so the types are already loaded and the container already has them.
/// </para>
///
/// <para>
/// Every lookup that fails says so and dispatches nothing. A miss here means the server
/// changed shape underneath, and the honest outcome is a download that stays unfinished
/// and a line in the log naming what could not be found - not an episode silently left
/// in a folder.
/// </para>
/// </summary>
public sealed class EncodeJobDispatch(IServiceProvider services, ILogger logger)
{
    private const string DispatcherType = "NoMercy.MediaProcessing.Jobs.IJobDispatcher";
    private const string JobType = "NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob";
    private const string LibraryRepositoryType = "NoMercy.MediaProcessing.Libraries.ILibraryRepository";

    /// <summary>
    /// Queues the encode for one file, and reports whether it was queued.
    ///
    /// <para>
    /// <paramref name="mediaId"/> is the id the file was matched to. The dashboard sends
    /// <c>match.id ?? 0</c>, so zero is a value the server already handles - it is what
    /// the operator's own screen sends for a file it could not match.
    /// </para>
    /// </summary>
    public async Task<bool> QueueAsync(Ulid libraryId, string inputFile, int mediaId, CancellationToken ct)
    {
        object? dispatcher = Resolve(DispatcherType);
        Type? jobType = FindType(JobType);

        if (dispatcher is null || jobType is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: the server does not expose {Missing}.",
                dispatcher is null ? DispatcherType : JobType);

            return false;
        }

        object? library = await LibraryAsync(libraryId, ct);

        if (library is null)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: library {Library} was not found.", libraryId);
            return false;
        }

        // The first folder of the library, which is what the Add content screen defaults
        // to - it reads folder_library[0] and offers the rest as a choice nobody has to
        // make. A library with no folder has nowhere to put this.
        object? folderId = FirstFolderId(library);

        if (folderId is null)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: library {Library} has no folder.", libraryId);
            return false;
        }

        object job = Activator.CreateInstance(jobType)!;

        Set(job, "LibraryId", libraryId);
        Set(job, "FolderId", folderId);
        Set(job, "Id", mediaId);
        Set(job, "InputFile", inputFile);
        Set(job, "PresetId", Get(library, "EncodePresetId"));

        MethodInfo? dispatch = dispatcher.GetType()
            .GetMethods()
            .FirstOrDefault(method => method.Name == "Dispatch" && method.GetParameters().Length == 3);

        if (dispatch is null)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: IJobDispatcher.Dispatch(job, queue, priority) is gone.");
            return false;
        }

        dispatch.Invoke(dispatcher, [job, Get(job, "QueueName"), Get(job, "Priority")]);

        logger.LogInformation("Torrent Downloader queued an encode for {File} in library {Library}.", inputFile, libraryId);

        return true;
    }

    private async Task<object?> LibraryAsync(Ulid libraryId, CancellationToken ct)
    {
        object? repository = Resolve(LibraryRepositoryType);

        MethodInfo? get = repository?.GetType()
            .GetMethods()
            .FirstOrDefault(method => method.Name.StartsWith("GetLibraryByIdLite", StringComparison.Ordinal));

        if (repository is null || get is null)
            return null;

        object? pending = get.Invoke(repository, Arguments(get, libraryId, ct));

        return pending is Task task ? await Unwrap(task) : pending;
    }

    private static object?[] Arguments(MethodInfo method, Ulid libraryId, CancellationToken ct) =>
        [
            .. method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken)
                    ? ct
                    : parameter.ParameterType == typeof(Ulid)
                        ? libraryId
                        : (object?)null),
        ];

    private static async Task<object?> Unwrap(Task task)
    {
        await task;

        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    /// <summary>
    /// The library's first folder id, read the way the dashboard reads it. The join row
    /// carries the id; the folder object beside it is for showing a path to a human.
    /// </summary>
    private static object? FirstFolderId(object library)
    {
        if (Get(library, "FolderLibraries") is not System.Collections.IEnumerable folders)
            return null;

        foreach (object? folder in folders)
        {
            if (folder is not null && Get(folder, "FolderId") is { } id)
                return id;
        }

        return null;
    }

    private object? Resolve(string typeName)
    {
        Type? type = FindType(typeName);

        return type is null ? null : services.GetService(type);
    }

    private static Type? FindType(string fullName) =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property)?.GetValue(target);

    private static void Set(object target, string property, object? value) =>
        target.GetType().GetProperty(property)?.SetValue(target, value);
}
