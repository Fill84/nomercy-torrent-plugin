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
    private readonly HostServices _host = new(services, logger);

    private const string DispatcherType = "NoMercy.MediaProcessing.Jobs.IJobDispatcher";
    private const string JobType = "NoMercy.MediaProcessing.Jobs.MediaJobs.VideoEncodeJob";
    /// <summary>
    /// There are two interfaces called <c>ILibraryRepository</c> in that process, and only
    /// this one is registered and only this one has the lookup.
    ///
    /// <para>
    /// The other, <c>NoMercy.MediaProcessing.Libraries.ILibraryRepository</c>, is what this
    /// asked for at first. It resolved to nothing - the server registers the concrete
    /// <c>MediaProcessingLibraryRepository</c> and never that interface - so every encode
    /// this plugin ever tried to queue was refused, and refused with the wrong sentence:
    /// "library X was not found" about a library that was right there. The short-name
    /// fallback cannot save this one either, because two types share the name, which is
    /// exactly when it declines to guess.
    /// </para>
    /// </summary>
    private const string LibraryRepositoryType = "NoMercy.Data.Repositories.ILibraryRepository";

    /// <summary>
    /// The service behind the Add content file browser: it walks a folder and matches every
    /// video in it to a movie or an episode the server already knows.
    ///
    /// <para>
    /// That match is the missing piece. <c>VideoEncodeJob</c> looks its media up by
    /// <c>Id.ToInt()</c> and returns without a word when nothing matches - so a job dispatched
    /// with no id is taken off the queue and does nothing, which is exactly what happened
    /// here: the queue's row counter moved, the encode never ran, and no line anywhere said
    /// why. The operator's screen does not guess that id either; it asks this, shows what came
    /// back, and sends it along. So does this.
    /// </para>
    /// </summary>
    private const string FileListServiceType = "NoMercy.MediaProcessing.Files.IFileListService";

    /// <summary>
    /// Queues the encode for one file, and reports whether it was queued.
    ///
    /// <para>
    /// Nothing here throws. An encode that cannot be queued is one download that stays
    /// unfinished and a line in the log saying why - it used to be an exception out of a
    /// reflection call, which unwound the whole transfers cadence, so a type mismatch on
    /// one job stopped every download in flight from being looked at at all.
    /// </para>
    /// </summary>
    public async Task<bool> QueueAsync(Ulid libraryId, string inputFile, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(libraryId, inputFile, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            logger.LogError(
                failure,
                "Torrent Downloader could not queue an encode for {File}. The download stays in the finished folder.",
                inputFile);

            return false;
        }
    }

    private async Task<bool> DispatchAsync(Ulid libraryId, string inputFile, CancellationToken ct)
    {
        object? dispatcher = _host.Resolve(DispatcherType);
        Type? jobType = _host.FindType(JobType);

        if (dispatcher is null || jobType is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: the server does not expose {Missing}. {Alternatives}",
                dispatcher is null ? DispatcherType : JobType,
                HostServices.WhatIsThere());

            return false;
        }

        object? library = await LibraryAsync(libraryId, ct);

        // LibraryAsync has already said which of the two things went wrong.
        if (library is null)
            return false;

        (object? folderId, string folderPath) = ChooseFolder(library);

        if (folderId is null)
            return false;

        // What the server itself thinks this file is. Asked the way the Add content screen
        // asks it, because the answer has to be the server's, not a guess assembled from a
        // filename by a plugin that cannot see the episode table.
        string? mediaId = await MatchAsync(inputFile, HostServices.Get(library, "Type") as string ?? "tv", ct);

        if (mediaId is null)
            return false;

        object job = Activator.CreateInstance(jobType)!;

        // Field for field, what ServerController.AddFiles sets. Nothing more, nothing
        // computed differently:
        //
        //   LibraryId    = library.Id            the show's library
        //   FolderId     = request.FolderId      the destination the dialog picks
        //   Id           = file.Id               the match the file list returned
        //   InputFile    = Path.GetFullPath(..)  expanded, because the source is local
        //   SourceDriverId                       left unset: that is what the controller
        //                                        does when no source_driver_id is sent, and
        //                                        a finished download is on this machine
        //   PresetId     = library.EncodePresetId
        //
        // Id is a string on AbstractEncoderJob, not a number. It was the int 0 here, which
        // threw out of the reflection call, and then an empty string, which made the job
        // find no episode and return in silence.
        if (!_host.Set(job, "LibraryId", libraryId)
            || !_host.Set(job, "FolderId", folderId)
            || !_host.Set(job, "Id", mediaId)
            || !_host.Set(job, "InputFile", Path.GetFullPath(inputFile)))
            return false;

        // Null keeps the folder's own presets, which is what a library with none set means.
        _host.Set(job, "PresetId", HostServices.Get(library, "EncodePresetId"));

        MethodInfo? dispatch = dispatcher.GetType()
            .GetMethods()
            .FirstOrDefault(method => method.Name == "Dispatch" && method.GetParameters().Length == 3);

        if (dispatch is null)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: IJobDispatcher.Dispatch(job, queue, priority) is gone.");
            return false;
        }

        dispatch.Invoke(dispatcher, [job, HostServices.Get(job, "QueueName"), HostServices.Get(job, "Priority")]);

        // Names the folder, because that is what the job resolves first and what it gives
        // up on silently when it cannot.
        logger.LogInformation(
            "Torrent Downloader queued an encode for {File} into folder {Folder} ({FolderId}) of library {Library}, preset {Preset}.",
            inputFile,
            folderPath,
            folderId,
            libraryId,
            HostServices.Get(library, "EncodePresetId")?.ToString() ?? "the folder's own");

        return true;
    }

    /// <summary>
    /// Which episode or film the server says this file is, as its own id.
    ///
    /// <para>
    /// The file browser lists a folder, shows the match beside every video, and the operator
    /// presses Add - and what travels to the server is that match's id, decided by the
    /// server, never by the screen. This does the same call on the folder the download was
    /// just moved into and reads the same field. It is not a shortcut around the operator's
    /// workflow; it is the operator's workflow with nobody having to be at the keyboard.
    /// </para>
    ///
    /// <para>
    /// Null when nothing matched, and it says so. A job with no id is one the encoder takes
    /// off the queue and silently drops, so dispatching one is worse than not: it reports
    /// success and leaves the episode in a folder nobody is watching.
    /// </para>
    /// </summary>
    private async Task<string?> MatchAsync(string inputFile, string libraryType, CancellationToken ct)
    {
        Type? contract = _host.FindType(FileListServiceType);

        if (contract is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: the server does not expose {Missing}, so nothing can say which episode {File} is.",
                FileListServiceType,
                inputFile);

            return null;
        }

        using IServiceScope scope = services.CreateScope();

        object? service = scope.ServiceProvider.GetService(contract);

        // Two arguments: the folder and the library's type. The three-argument overload
        // takes a storage driver, which is for a folder on a remote share - a finished
        // download is on this machine.
        MethodInfo? list = service?.GetType()
            .GetMethods()
            .FirstOrDefault(method => method.Name == "GetFilesInDirectory" && method.GetParameters().Length == 2);

        if (service is null || list is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: {Contract} resolved to {Service} and GetFilesInDirectory(folder, type) {Found}.",
                FileListServiceType,
                service?.GetType().FullName ?? "nothing",
                list is null ? "is not on it" : "is");

            return null;
        }

        string folder = Path.GetDirectoryName(inputFile) ?? inputFile;

        if (list.Invoke(service, [folder, libraryType]) is not Task pending)
            return null;

        if (await HostServices.Unwrap(pending) is not System.Collections.IEnumerable items)
            return null;

        foreach (object? item in items)
        {
            if (item is null || HostServices.Get(item, "Path") is not string path)
                continue;

            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(inputFile), StringComparison.OrdinalIgnoreCase))
                continue;

            // Id is declared dynamic on the match, so it arrives boxed as whatever the
            // provider put there. The job wants the string form either way.
            string? id = (HostServices.Get(item, "Match") is { } match ? HostServices.Get(match, "Id") : null)?.ToString();

            if (!string.IsNullOrWhiteSpace(id) && id != "0")
                return id;

            logger.LogWarning(
                "Torrent Downloader will not queue an encode for {File}: the server scanned it and matched no episode. It stays in the finished folder.",
                inputFile);

            return null;
        }

        logger.LogWarning(
            "Torrent Downloader will not queue an encode for {File}: the server's own scan of {Folder} did not list it.",
            inputFile,
            folder);

        return null;
    }

    /// <summary>
    /// The library, or null with a line saying which of the two things went wrong.
    ///
    /// <para>
    /// It used to answer null for both "the repository is not there" and "the library is
    /// not there", and the caller reported the second. So a wiring mistake was logged, for
    /// days, as a fact about the owner's library that was not true.
    /// </para>
    ///
    /// <para>
    /// Resolved inside a scope. The repository is registered scoped, because it opens a
    /// database context, and a scoped service asked of the root provider is either an
    /// exception or a null - never the object. The plugin's cadences are not requests and
    /// have no scope of their own, so this makes one.
    /// </para>
    /// </summary>
    private async Task<object?> LibraryAsync(Ulid libraryId, CancellationToken ct)
    {
        Type? contract = _host.FindType(LibraryRepositoryType);

        if (contract is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: the server does not expose {Missing}.",
                LibraryRepositoryType);

            return null;
        }

        using IServiceScope scope = services.CreateScope();

        object? repository = scope.ServiceProvider.GetService(contract);

        // The full lookup, not the Lite one. Lite selects the row and includes nothing, so
        // the library came back real and folderless and the encode was refused for having
        // nowhere to go - on a library with two folders. This one includes
        // FolderLibraries, which is the whole reason it is being asked.
        MethodInfo? get = HostServices.Method(repository, "GetLibraryByIdAsync") ?? HostServices.Method(repository, "GetLibraryById");

        if (repository is null || get is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: {Contract} resolved to {Repository}, and no GetLibraryById method {Found}.",
                LibraryRepositoryType,
                repository?.GetType().FullName ?? "nothing",
                get is null ? "is on it" : "is");

            return null;
        }

        object? pending = get.Invoke(repository, HostServices.Arguments(get, libraryId, ct));

        return pending is Task task ? await HostServices.Unwrap(task) : pending;
    }

    /// <summary>
    /// The library's first folder - the destination the Add content dialog offers before
    /// anybody changes it, and what goes to the job as <c>FolderId</c>.
    ///
    /// <para>
    /// The first one, and no opinion about it. This briefly preferred a folder whose
    /// <c>Folders.Path</c> was not empty, on the reasoning that an empty one could not be a
    /// real destination. It can: the second folder of a real library is a <c>Z:</c> drive
    /// whose location lives on its storage driver rather than in that column, and the
    /// dialog lists it happily. Which folder is a good destination is the owner's business
    /// and the server's, and a plugin ranking them produces a different answer from the
    /// button beside it.
    /// </para>
    ///
    /// <para>
    /// The path comes back only to be logged.
    /// </para>
    /// </summary>
    private (object? Id, string Path) ChooseFolder(object library)
    {
        if (HostServices.Get(library, "FolderLibraries") is not System.Collections.IEnumerable rows)
            return (null, string.Empty);

        List<(object Id, string Path)> candidates = [];

        foreach (object? row in rows)
        {
            if (row is null || HostServices.Get(row, "FolderId") is not { } id)
                continue;

            string path = HostServices.Get(row, "Folder") is { } folder ? HostServices.Get(folder, "Path") as string ?? string.Empty : string.Empty;

            candidates.Add((id, path));
        }

        if (candidates.Count == 0)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: this library has no folder.");
            return (null, string.Empty);
        }

        return candidates[0];
    }

}
