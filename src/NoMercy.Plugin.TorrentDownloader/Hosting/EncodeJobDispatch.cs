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
    public async Task<bool> QueueAsync(Ulid libraryId, string inputFile, string folderId, CancellationToken ct)
    {
        try
        {
            return await DispatchAsync(libraryId, inputFile, folderId, ct);
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

    private async Task<bool> DispatchAsync(Ulid libraryId, string inputFile, string folderId, CancellationToken ct)
    {
        object? dispatcher = Resolve(DispatcherType);
        Type? jobType = FindType(JobType);

        if (dispatcher is null || jobType is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: the server does not expose {Missing}. {Alternatives}",
                dispatcher is null ? DispatcherType : JobType,
                WhatIsThere());

            return false;
        }

        object? library = await LibraryAsync(libraryId, ct);

        // LibraryAsync has already said which of the two things went wrong.
        if (library is null)
            return false;

        (object? chosenFolder, string folderPath) = ChooseFolder(library, folderId);

        if (chosenFolder is null)
            return false;

        // What the server itself thinks this file is. Asked the way the Add content screen
        // asks it, because the answer has to be the server's, not a guess assembled from a
        // filename by a plugin that cannot see the episode table.
        string? mediaId = await MatchAsync(inputFile, Get(library, "Type") as string ?? "tv", ct);

        if (mediaId is null)
            return false;

        object job = Activator.CreateInstance(jobType)!;

        // Id is a string on AbstractEncoderJob, not a number, and it is the media this file
        // was matched to. Empty is what an unmatched file carries - and an unmatched file is
        // a job that runs and does nothing, so it never gets dispatched from here.
        if (!Set(job, "LibraryId", libraryId)
            || !Set(job, "FolderId", chosenFolder)
            || !Set(job, "InputFile", inputFile)
            || !Set(job, "Id", mediaId))
            return false;

        // Optional: a library with no preset encodes with the server's defaults.
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

        // Names the folder, because that is what the job resolves first and what it gives
        // up on silently when it cannot.
        logger.LogInformation(
            "Torrent Downloader queued an encode for {File} into folder {Folder} ({FolderId}) of library {Library}, preset {Preset}.",
            inputFile,
            folderPath,
            chosenFolder,
            libraryId,
            Get(library, "EncodePresetId")?.ToString() ?? "the folder's own");

        return true;
    }

    /// <summary>
    /// Every folder the server's libraries hold, so the owner can pick the one downloads
    /// are encoded into - the same choice the Add content screen puts in front of them.
    ///
    /// <para>
    /// Each entry is the folder's id and a label naming its library and its path. The list
    /// comes from the server every time the settings page is drawn: a folder the owner has
    /// since removed must stop being offered, and one they have just added must appear
    /// without this plugin being restarted.
    /// </para>
    /// </summary>
    public async Task<IReadOnlyList<(string Id, string Label)>> FoldersAsync(
        IReadOnlyList<(string Id, string Title)> libraries,
        CancellationToken ct)
    {
        List<(string, string)> folders = [];

        foreach ((string id, string title) in libraries)
        {
            if (!Ulid.TryParse(id, out Ulid libraryId) || await LibraryAsync(libraryId, ct) is not { } library)
                continue;

            if (Get(library, "FolderLibraries") is not System.Collections.IEnumerable rows)
                continue;

            foreach (object? row in rows)
            {
                if (row is null || Get(row, "FolderId") is not { } folderId)
                    continue;

                string path = Get(row, "Folder") is { } folder ? Get(folder, "Path") as string ?? string.Empty : string.Empty;

                folders.Add((folderId.ToString()!, path.Length > 0 ? $"{title} - {path}" : $"{title} - (no path)"));
            }
        }

        return folders;
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
        Type? contract = FindType(FileListServiceType);

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

        if (await Unwrap(pending) is not System.Collections.IEnumerable items)
            return null;

        foreach (object? item in items)
        {
            if (item is null || Get(item, "Path") is not string path)
                continue;

            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(inputFile), StringComparison.OrdinalIgnoreCase))
                continue;

            // Id is declared dynamic on the match, so it arrives boxed as whatever the
            // provider put there. The job wants the string form either way.
            string? id = (Get(item, "Match") is { } match ? Get(match, "Id") : null)?.ToString();

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
        Type? contract = FindType(LibraryRepositoryType);

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
        MethodInfo? get = Method(repository, "GetLibraryByIdAsync") ?? Method(repository, "GetLibraryById");

        if (repository is null || get is null)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: {Contract} resolved to {Repository}, and no GetLibraryById method {Found}.",
                LibraryRepositoryType,
                repository?.GetType().FullName ?? "nothing",
                get is null ? "is on it" : "is");

            return null;
        }

        object? pending = get.Invoke(repository, Arguments(get, libraryId, ct));

        return pending is Task task ? await Unwrap(task) : pending;
    }

    /// <summary>
    /// A method by exact name, then by prefix, taking only ones this can actually call -
    /// every parameter is either the id or a cancellation token, and anything else would be
    /// passed null and mean something nobody here intended.
    /// </summary>
    private static MethodInfo? Method(object? target, string name) =>
        target?.GetType()
            .GetMethods()
            .Where(method => method.Name.StartsWith(name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().All(parameter =>
                parameter.ParameterType == typeof(Ulid) || parameter.ParameterType == typeof(CancellationToken)))
            .OrderBy(method => method.Name.Length)
            .FirstOrDefault();

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
    /// The folder this encode goes into: the one the owner picked, or the library's first,
    /// which is what the Add content screen offers before anybody changes it.
    ///
    /// <para>
    /// Nothing here judges the folder. Which folder is a good destination is the owner's
    /// business and the server's - this plugin's job is to hand the encode the same fields
    /// that screen hands it, and a plugin second-guessing them is a plugin producing a
    /// different result from the button next to it.
    /// </para>
    /// </summary>
    private (object? Id, string Path) ChooseFolder(object library, string preferredFolderId)
    {
        if (Get(library, "FolderLibraries") is not System.Collections.IEnumerable rows)
            return (null, string.Empty);

        List<(object Id, string Path)> candidates = [];

        foreach (object? row in rows)
        {
            if (row is null || Get(row, "FolderId") is not { } id)
                continue;

            string path = Get(row, "Folder") is { } folder ? Get(folder, "Path") as string ?? string.Empty : string.Empty;

            candidates.Add((id, path));
        }

        if (candidates.Count == 0)
        {
            logger.LogError("Torrent Downloader cannot queue an encode: this library has no folder.");
            return (null, string.Empty);
        }

        if (preferredFolderId.Length > 0)
        {
            (object Id, string Path) picked = candidates.FirstOrDefault(candidate =>
                string.Equals(candidate.Id.ToString(), preferredFolderId, StringComparison.OrdinalIgnoreCase));

            if (picked.Id is not null)
                return picked;

            // Said out loud rather than quietly falling back: a chosen folder that has gone
            // is a setting pointing at nothing, and the owner is the only one who can fix it.
            logger.LogWarning(
                "Torrent Downloader cannot use the chosen folder {Folder} - it is not on this library. Using its first folder instead.",
                preferredFolderId);
        }

        return candidates[0];
    }

    private object? Resolve(string typeName)
    {
        Type? type = FindType(typeName);

        return type is null ? null : services.GetService(type);
    }

    /// <summary>
    /// The type by its full name, or failing that by its short name anywhere.
    ///
    /// <para>
    /// The fallback is what keeps this working when the server rearranges its namespaces,
    /// which is a refactor nobody would think to tell a plugin about. There is exactly one
    /// <c>IJobDispatcher</c> in that process and exactly one <c>VideoEncodeJob</c>, so a
    /// short-name match is not a guess. Two of either would be ambiguous, and the type
    /// then stays unresolved rather than being picked at random - a named failure beats a
    /// coin toss over which encoder runs.
    /// </para>
    /// </summary>
    /// <summary>
    /// The job and dispatcher types that <em>do</em> exist in the process, for the log line
    /// that reports a miss.
    ///
    /// <para>
    /// A plugin ships as a file somebody drops next to a server they did not build, and the
    /// server is a single-file bundle whose type names cannot be read from outside it. So a
    /// miss has to answer its own question: not "it is not there", but "it is not there,
    /// and here is what is". That turns diagnosing this from a restart per guess into one
    /// restart.
    /// </para>
    /// </summary>
    private static string WhatIsThere()
    {
        List<string> candidates =
        [
            .. AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(Types)
                .Where(type => type.Name.EndsWith("EncodeJob", StringComparison.Ordinal)
                    || type.Name.EndsWith("JobDispatcher", StringComparison.Ordinal))
                .Select(type => type.FullName ?? type.Name)
                .Distinct()
                .Order()
                .Take(20),
        ];

        return candidates.Count == 0
            ? "Nothing in this process looks like a job dispatcher or an encode job at all."
            : $"What the process does have: {string.Join(", ", candidates)}.";
    }

    private Type? FindType(string fullName)
    {
        Assembly[] loaded = AppDomain.CurrentDomain.GetAssemblies();

        Type? exact = loaded
            .Select(assembly => assembly.GetType(fullName, throwOnError: false))
            .FirstOrDefault(type => type is not null);

        if (exact is not null)
            return exact;

        string shortName = fullName[(fullName.LastIndexOf('.') + 1)..];

        // Only reached when the full name missed, so the cost of walking every assembly is
        // paid once on a server that moved the type and never on one that did not.
        List<Type> named = [.. loaded.SelectMany(Types).Where(type => type.Name == shortName).Distinct()];

        if (named.Count != 1)
            return null;

        logger.LogWarning(
            "Torrent Downloader found {Short} at {Actual} rather than {Expected}. The server moved it; this still works, but the plugin is out of date.",
            shortName,
            named[0].FullName,
            fullName);

        return named[0];
    }

    private static IEnumerable<Type> Types(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException)
        {
            // An assembly whose dependencies are not all loaded cannot be walked, and is
            // not the one holding the server's own job types either.
            return [];
        }
    }

    private static object? Get(object target, string property) =>
        target.GetType().GetProperty(property)?.GetValue(target);

    /// <summary>
    /// Sets a property, and says whether it took.
    ///
    /// <para>
    /// It used to be void, which made a missing property and a wrong type look the same
    /// from the caller: one did nothing quietly, the other threw out through the cadence.
    /// Both now answer false, and the caller refuses to dispatch a job it could not fill in
    /// - a half-built encode job is worse than no encode job.
    /// </para>
    /// </summary>
    private bool Set(object target, string property, object? value)
    {
        PropertyInfo? slot = target.GetType().GetProperty(property);

        if (slot is null || !slot.CanWrite)
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: {Job} has no writable {Property}.",
                target.GetType().Name,
                property);

            return false;
        }

        Type wanted = Nullable.GetUnderlyingType(slot.PropertyType) ?? slot.PropertyType;

        if (value is not null && !wanted.IsInstanceOfType(value))
        {
            logger.LogError(
                "Torrent Downloader cannot queue an encode: {Job}.{Property} is {Wanted}, and this offered {Offered}.",
                target.GetType().Name,
                property,
                wanted.Name,
                value.GetType().Name);

            return false;
        }

        slot.SetValue(target, value);

        return true;
    }
}
