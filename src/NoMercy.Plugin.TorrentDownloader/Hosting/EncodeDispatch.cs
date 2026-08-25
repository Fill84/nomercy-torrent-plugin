using System.Collections;
using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NoMercy.Plugin.TorrentDownloader.Core.Activity;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

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
    : IEncodeGateway
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

    /// <summary>The server's own database, where the episode rows are.</summary>
    /// <remarks>
    /// Asked directly because the plugin knows which episode it downloaded and
    /// the server does not: the file list identifies a file by searching a
    /// catalogue for the title it reads out of the name, which came back empty
    /// for every episode the owner staged on 24 August 2026 — while the row it
    /// wanted was in this table the whole time.
    /// </remarks>
    public const string ContextType = "NoMercy.Database.MediaContext";

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
    /// <inheritdoc/>
    public async Task<bool> DispatchAsync(
        string stagedFile,
        EpisodeKey episode,
        Show show,
        string? existing,
        CancellationToken ct)
    {
        // The show's own library, and its type spelled the way the file list
        // service wants it. Both are read off the show rather than asked for
        // separately: the server decided the media type when the show was
        // filed, and this plugin follows it rather than choosing. Turning the
        // one into the other is host vocabulary, so it happens here at the edge
        // and not in the cadence.
        string libraryId = show.LibraryId;
        string libraryType = show.Kind == LibraryKind.Anime ? "anime" : "tv";

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
            if (Where(Read(library, "FolderLibraries"), existing) is not object folder)
            {
                return Refused($"library {libraryId} has no folder to put anything in");
            }

            string full = Path.GetFullPath(stagedFile);

            // From the server, never from the filename. A job whose id matches
            // no media is dropped in silence: the queue counter moves and the
            // encode never runs.
            Matched matched = await MatchedAsync(files, Path.GetDirectoryName(full)!, libraryType, full)
                .ConfigureAwait(false);

            if (matched.Listed is null)
            {
                return Refused($"the server would not list the folder {full} is in");
            }

            if (!matched.Ours)
            {
                // Not in the listing means the server skips it, and it skips a
                // file its own parser cannot read a title out of. Dispatching
                // then would queue a job for a file the encoder will not find.
                return Refused(
                    $"the server listed {matched.Listed} file(s) in that folder and {full} was not among them");
            }

            // The plugin's own answer first. It knows the show, the season and
            // the number it went and downloaded; the listing works the same
            // thing out backwards from the file's name and can be wrong about
            // it, and on the owner's server it was wrong about every one.
            string? id = EpisodeId(scope.ServiceProvider, episode) ?? matched.Id;

            if (id is null)
            {
                // Listed, and matched to no media. The dashboard's own Add
                // content dispatches this case anyway — where the id is empty
                // it lets the file through — so this does too. Being stricter
                // than the server's own interface is not a safety: it made a
                // file the owner could add by hand one the plugin would not.
                //
                // Said out loud, because a job the encoder can do nothing with
                // is not something to discover from a library that stays empty.
                logger.LogWarning(
                    "{File} was listed and matched no media, and was dispatched anyway, as Add content does — {Said}.",
                    full,
                    matched.Said ?? "and said nothing about it");

                journal.Failed(
                    ActivityStage.Dispatch,
                    Path.GetFileName(full),
                    "the server matched no media to it, and it was dispatched anyway");
            }

            object job = Activator.CreateInstance(jobType)
                         ?? throw new InvalidOperationException("the encode job could not be constructed");

            Write(job, "LibraryId", libraryId);
            Write(job, "FolderId", Read(folder, "FolderId"));
            // Whatever the listing gave, empty included: that is what the
            // dashboard sends and the server is what decides.
            Write(job, "Id", id ?? string.Empty);
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
    /// <remarks>
    /// <para>
    /// <strong>The listing is a task.</strong> The server answers
    /// <c>Task&lt;List&lt;FileItem&gt;&gt;</c>, and walking the returned object
    /// as a list walks the <c>Task</c> and finds nothing — so every staged
    /// episode was answered with "the server matched no media to this file" and
    /// no encode was ever dispatched. It is awaited now.
    /// </para>
    /// <para>
    /// An id of nothing is no match. <c>MovieOrEpisode.Id</c> is a
    /// <c>dynamic</c> that starts as an empty string, so a file the server
    /// listed but could not identify comes back with one — and a job carrying
    /// it is dropped by the encoder in silence while the queue counter moves.
    /// </para>
    /// </remarks>
    private static async Task<Matched> MatchedAsync(object files, string folder, string libraryType, string full)
    {
        // The two-argument overload. The three-argument one takes a storage
        // driver and is for a folder on a remote share.
        MethodInfo? walk = files.GetType()
            .GetMethods()
            .FirstOrDefault(one => one.Name == "GetFilesInDirectory" && one.GetParameters().Length == 2);

        if (walk is null)
        {
            return new(null, false, null);
        }

        if (await Awaited(walk.Invoke(files, [folder, libraryType])).ConfigureAwait(false) is not IEnumerable found)
        {
            return new(null, false, null);
        }

        int listed = 0;
        bool ours = false;
        string? said = null;

        foreach (object? item in found)
        {
            listed++;

            if (item is null || Read(item, "Path") is not string path)
            {
                continue;
            }

            if (!string.Equals(Path.GetFullPath(path), full, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ours = true;

            string? id = Read(item, "Match") is object match ? Read(match, "Id")?.ToString() : null;

            if (!string.IsNullOrWhiteSpace(id) && id != "0")
            {
                return new(listed, true, id);
            }

            // What the server made of it, in its own words. Without this a
            // dispatch that matched nothing says only that it matched nothing,
            // and the reason — the title it read, the season and episode it
            // took from the name — is nowhere. Two days were spent inferring
            // it from a log that already had everything but this.
            said = Said(item);
        }

        return new(listed, ours, null, said);
    }

    /// <summary>What the server said about the folder this file is in.</summary>
    /// <param name="Listed">How many files it listed, or null when it would not answer.</param>
    /// <param name="Ours">Whether ours was among them.</param>
    /// <param name="Id">The server's own id for it, when it has one.</param>
    /// <param name="Said">What it parsed out of a file it could not match.</param>
    private sealed record Matched(int? Listed, bool Ours, string? Id, string? Said = null);

    /// <summary>What the server parsed out of a file it could not match.</summary>
    /// <remarks>
    /// Read off its own parse rather than worked out again here: the point is
    /// to say what the server believed, which is the only thing that explains
    /// why it matched nothing.
    /// </remarks>
    private static string Said(object item)
    {
        if (Read(item, "Parsed") is not object parsed)
        {
            return "the server offered no parse of it";
        }

        return string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"it read title {Read(parsed, "Title") ?? "nothing"}, "
            + $"year {Read(parsed, "Year") ?? "nothing"}, "
            + $"season {Read(parsed, "Season") ?? "nothing"}, "
            + $"episode {Read(parsed, "Episode") ?? "nothing"}");
    }

    private bool Refused(string reason)
    {
        logger.LogWarning("No encode was dispatched: {Reason}.", reason);
        journal.Failed(ActivityStage.Download, "encode", reason);

        return false;
    }

    /// <summary>
    /// One method by name, chosen by how many arguments it takes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetMethod(name)</c> throws <c>AmbiguousMatchException</c> the moment
    /// the server has two methods of that name, and it has:
    /// <c>GetLibraryByIdAsync</c> takes either an id or an id and six more
    /// arguments. So every dispatch ended in the catch below and answered
    /// "No encode was dispatched: Ambiguous match found" — which is why, on
    /// 23 August 2026, not one encode had ever been queued.
    /// </para>
    /// <para>
    /// The arguments are converted to what the method actually takes. Ids cross
    /// this boundary as text, because that is how the plugin's own contract
    /// spells them, and arrive at a server that deals in <c>Ulid</c>.
    /// </para>
    /// </remarks>
    private static object? Call(object instance, string method, params object?[] arguments)
    {
        MethodInfo? wanted = instance.GetType()
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .FirstOrDefault(one =>
                string.Equals(one.Name, method, StringComparison.Ordinal)
                && one.GetParameters().Length == arguments.Length);

        if (wanted is null)
        {
            return null;
        }

        ParameterInfo[] takes = wanted.GetParameters();

        return wanted.Invoke(
            instance,
            [.. arguments.Select((object? argument, int at) => Converted(argument, takes[at].ParameterType))]);
    }

    /// <summary>
    /// A value as the type it is being handed to wants it.
    /// </summary>
    /// <remarks>
    /// Through the target type's own <c>Parse</c>, never through a type this
    /// plugin names: the server's <c>Ulid</c> and any this plugin referenced
    /// would be two different types to the runtime however identically they are
    /// spelled, and assigning one to the other fails in a way no message
    /// explains.
    /// </remarks>
    private static object? Converted(object? value, Type wanted)
    {
        if (value is null || wanted.IsInstanceOfType(value))
        {
            return value;
        }

        Type bare = Nullable.GetUnderlyingType(wanted) ?? wanted;

        if (value is string text)
        {
            MethodInfo? parse = bare.GetMethod(
                "Parse",
                BindingFlags.Public | BindingFlags.Static,
                null,
                [typeof(string)],
                null);

            if (parse is not null)
            {
                return parse.Invoke(null, [text]);
            }
        }

        return Convert.ChangeType(value, bare, CultureInfo.InvariantCulture);
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

    /// <summary>The server's own id for one episode, from its own table.</summary>
    /// <remarks>
    /// <para>
    /// The encode job carries a media id and does <c>Id.ToInt()</c> with it to
    /// find the row to register the encode against. An empty id becomes 0,
    /// matches nothing, and the job ends with "Post-encode registration found
    /// 0 files": the queue counter moves and the library stays empty. That is
    /// what happened to every episode the owner staged on 24 August 2026.
    /// </para>
    /// <para>
    /// It used to come from the file list, which identifies a file by searching
    /// a catalogue for the title it reads out of the name. The plugin does not
    /// need to be told: it chose the show, the season and the number, so it
    /// asks for that row by those three.
    /// </para>
    /// <para>
    /// Filtered by the database and not here. The set is every episode the
    /// owner has, tens of thousands of rows, and the server indexes exactly
    /// this triple — so it is the question the table was built to answer.
    /// </para>
    /// <para>
    /// Null when the server has no such row or does not hand the database over
    /// at all, and then the listing's answer is used as before. This reaches
    /// past the plugin contract, which carries no episode id; the contract
    /// ought to, and that is media-server issue territory rather than a reason
    /// to leave the owner's library empty.
    /// </para>
    /// </remarks>
    private static string? EpisodeId(IServiceProvider scope, EpisodeKey episode)
    {
        if (Named(ContextType) is not Type contextType
            || scope.GetService(contextType) is not object context
            || Read(context, "Episodes") is not IQueryable rows)
        {
            return null;
        }

        ParameterExpression one = Expression.Parameter(rows.ElementType, "episode");
        Expression? same = null;

        foreach ((string column, int value) in new[]
                 {
                     ("TvId", episode.ShowId),
                     ("SeasonNumber", episode.Season),
                     ("EpisodeNumber", episode.Number),
                 })
        {
            if (rows.ElementType.GetProperty(column) is not PropertyInfo held)
            {
                return null;
            }

            BinaryExpression equal = Expression.Equal(
                Expression.Property(one, held),
                Expression.Constant(value));

            same = same is null ? equal : Expression.AndAlso(same, equal);
        }

        // The overload that takes a predicate. There is a second two-argument
        // one taking a default value, and picking by count alone chose that —
        // which answers "cannot convert Expression to Episode" rather than
        // asking the database anything.
        MethodInfo first = typeof(Queryable)
            .GetMethods()
            .First(method => method.Name == nameof(Queryable.FirstOrDefault)
                             && method.GetParameters().Length == 2
                             && method.GetParameters()[1].ParameterType.IsGenericType
                             && method.GetParameters()[1].ParameterType.GetGenericTypeDefinition()
                             == typeof(Expression<>))
            .MakeGenericMethod(rows.ElementType);

        object? found = first.Invoke(null, [rows, Expression.Lambda(same!, one)]);

        return found is null ? null : Read(found, "Id")?.ToString();
    }

    private static object? Read(object instance, string property)
    {
        return instance.GetType().GetProperty(property)?.GetValue(instance);
    }

    private static void Write(object instance, string property, object? value)
    {
        PropertyInfo? target = instance.GetType().GetProperty(property);

        target?.SetValue(instance, Converted(value, target.PropertyType));
    }

    /// <summary>
    /// The library folder this show's episodes really live in.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A library can have several, on different drives. The owner's has two,
    /// and taking the first sent every encode to a drive the server could not
    /// reach: every job failed with "could not find a part of the path". The
    /// dashboard's own Add content does not guess — it sends the folder the
    /// person browsing chose.
    /// </para>
    /// <para>
    /// So the folder is the one an episode of this show is already in. Falling
    /// back to the first is only for a show with nothing on disk, which is not
    /// a show this plugin downloads for.
    /// </para>
    /// </remarks>
    private static object? Where(object? folders, string? existing)
    {
        if (folders is not IEnumerable list)
        {
            return null;
        }

        object? first = null;

        foreach (object? entry in list)
        {
            first ??= entry;

            if (entry is null || string.IsNullOrWhiteSpace(existing))
            {
                continue;
            }

            if (Read(entry, "Folder") is object folder
                && Read(folder, "Path") is string path
                && !string.IsNullOrWhiteSpace(path)
                && Under(existing, path))
            {
                return entry;
            }
        }

        return first;
    }

    /// <summary>Whether a file sits under a folder, however either spells its separators.</summary>
    private static bool Under(string file, string folder)
    {
        string one = file.Replace('\\', '/');
        string other = folder.Replace('\\', '/').TrimEnd('/');

        return one.StartsWith(other + "/", StringComparison.OrdinalIgnoreCase);
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
