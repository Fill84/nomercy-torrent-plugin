using System.Globalization;
using System.Reflection;

using Microsoft.Extensions.Logging;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Adds a show to one of the owner's libraries, the way the dashboard does.
/// </summary>
/// <remarks>
/// <para>
/// A torrent for a show the owner does not have could not be dispatched at all:
/// an encode is asked for by the server's own episode id, and a show with no
/// row has no episodes and no ids. The plugin handed the files over with no id
/// instead and asked the server to work them out, and on 31 August 2026 that
/// took nine files, answered that all nine jobs had finished within two
/// minutes, and wrote nothing to the library at all.
/// </para>
/// <para>
/// <strong>This is the same route the dashboard takes.</strong> Its Add content
/// searches the metadata providers, the owner picks one, and the assignment
/// ends in <c>DispatchJob&lt;ShowImportJob&gt;(tmdbId, libraryId)</c> — that one
/// call is what puts a show in a library. Nothing here invents anything: it
/// asks the same probe and dispatches the same job.
/// </para>
/// <para>
/// <strong>It reaches the server by name, and that is deliberate.</strong>
/// None of this is in <c>NoMercy.Plugins.Abstractions</c>: the contract can
/// name an episode the server already has and can offer no way to add a show,
/// so there is nothing to call. The owner asked for this rather than for
/// another refusal. Every step is guarded and every failure is one line the
/// owner can read, because a server that renames one of these types will
/// break it — and when it does, the plugin says so and carries on rather than
/// falling over.
/// </para>
/// </remarks>
public sealed class ShowAdmission(IServiceProvider services, ILogger logger)
{
    private const string ProbeType = "NoMercy.MediaProcessing.Inbox.IInboxMetadataProbe";

    private const string DispatcherType = "NoMercy.MediaProcessing.Jobs.IJobDispatcher";

    private const string ImportJobType = "NoMercy.MediaProcessing.Jobs.MediaJobs.ShowImportJob";

    /// <summary>
    /// Says whether this server has the parts, before anything needs them.
    /// </summary>
    /// <remarks>
    /// Asked once when the plugin wakes, because otherwise the answer arrives
    /// only when a torrent for an unknown show finishes — which can be an hour
    /// of downloading away, and is the worst moment to find out that the three
    /// things this needs are not there. It names them, so a server that renamed
    /// one can be told from a server that never had it.
    /// </remarks>
    public void Ready()
    {
        string[] missing =
        [
            .. new[] { ProbeType, DispatcherType, ImportJobType }
                .Where(one => Find(one) is null),
        ];

        if (missing.Length > 0)
        {
            logger.LogWarning(
                "A show this owner does not have cannot be added on this server: it offers no {Missing}. "
                + "A torrent for one is handed over to be identified instead.",
                string.Join(", ", missing));

            return;
        }

        if (Resolve(ProbeType) is null || Resolve(DispatcherType) is null)
        {
            logger.LogWarning(
                "This server has the parts that add a show but does not hand them out, "
                + "so a torrent for a show the owner does not have is handed over to be identified instead.");

            return;
        }

        logger.LogInformation(
            "A torrent for a show the owner does not have will be looked up and added: "
            + "this server offers the metadata probe and the import job both.");
    }

    /// <summary>
    /// Looks a show up with the server's own providers and adds it.
    /// </summary>
    /// <param name="title">The show's title, as the release name spells it.</param>
    /// <param name="year">Its year where the release name carries one.</param>
    /// <param name="library">Which library it goes in.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What it added, or null when it could not.</returns>
    public async Task<string?> AddAsync(string title, int? year, Library library, CancellationToken ct)
    {
        object? probe = Resolve(ProbeType);
        object? dispatcher = Resolve(DispatcherType);
        Type? job = Find(ImportJobType);

        if (probe is null || dispatcher is null || job is null)
        {
            logger.LogWarning(
                "{Title} is in no library and this server does not offer the parts that add one, "
                + "so nothing could be added for it.",
                title);

            return null;
        }

        object? found = await SearchAsync(probe, title, year, ct).ConfigureAwait(false);

        if (found is null)
        {
            return null;
        }

        string? external = Read<string>(found, "ExternalId");
        string? matched = Read<string>(found, "Title");

        if (external is null || !int.TryParse(external, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            logger.LogWarning("The provider named {Title} without an id this server can import by.", title);

            return null;
        }

        return Dispatch(dispatcher, job, id, library, matched ?? title);
    }

    /// <summary>Asks the server's providers, and takes the nearest answer.</summary>
    /// <remarks>
    /// The year decides where there is one: a title on its own matches remakes
    /// and reboots, and putting the wrong show in a library is worse than
    /// putting none — the first is a thing the owner has to find and undo.
    /// </remarks>
    private async Task<object?> SearchAsync(object probe, string title, int? year, CancellationToken ct)
    {
        MethodInfo? search = probe.GetType().GetMethod(
            "SearchTvAsync",
            [typeof(string), typeof(int?), typeof(CancellationToken)]);

        if (search is null)
        {
            logger.LogWarning("This server's metadata probe has no SearchTvAsync, so no show could be looked up.");

            return null;
        }

        object[]? candidates;

        try
        {
            if (search.Invoke(probe, [title, year, ct]) is not Task asking)
            {
                return null;
            }

            await asking.ConfigureAwait(false);

            candidates = (asking.GetType().GetProperty("Result")?.GetValue(asking) as Array)?
                .Cast<object>()
                .ToArray();
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            logger.LogWarning("{Title} could not be looked up: {Reason}", title, refused.Message);

            return null;
        }

        if (candidates is null || candidates.Length == 0)
        {
            logger.LogInformation("No provider knows a show called {Title}, so nothing was added.", title);

            return null;
        }

        // The year where the release name carries one, and the provider's own
        // order otherwise — it ranks by how well the title matched.
        object? chosen = year is int wanted
            ? candidates.FirstOrDefault(one => Read<int?>(one, "Year") == wanted)
            : null;

        chosen ??= candidates[0];

        return chosen;
    }

    /// <summary>Dispatches the import, which is what actually adds the show.</summary>
    private string? Dispatch(object dispatcher, Type job, int id, Library library, string matched)
    {
        MethodInfo? dispatch = dispatcher
            .GetType()
            .GetMethods()
            .FirstOrDefault(one =>
                one.Name == "DispatchJob"
                && one.IsGenericMethodDefinition
                && one.GetGenericArguments().Length == 1
                && one.GetParameters() is [{ ParameterType.Name: "Int32" }, { ParameterType.Name: "Ulid" }]);

        if (dispatch is null)
        {
            logger.LogWarning("This server's dispatcher has no DispatchJob(int, Ulid), so no show could be imported.");

            return null;
        }

        try
        {
            object? libraryId = dispatch.GetParameters()[1].ParameterType
                .GetMethod("Parse", [typeof(string), typeof(IFormatProvider)])?
                .Invoke(null, [library.Id, CultureInfo.InvariantCulture]);

            if (libraryId is null)
            {
                logger.LogWarning("{Library} is not an id this server can read.", library.Name);

                return null;
            }

            dispatch.MakeGenericMethod(job).Invoke(dispatcher, [id, libraryId]);
        }
        catch (Exception refused) when (refused is not OperationCanceledException)
        {
            logger.LogWarning("{Title} could not be imported: {Reason}", matched, refused.Message);

            return null;
        }

        logger.LogInformation(
            "{Title} was in no library, so it was looked up and added to {Library} as {Id}.",
            matched,
            library.Name,
            id);

        return matched;
    }

    /// <summary>One of the server's services, or null on a server without it.</summary>
    private object? Resolve(string name)
    {
        return Find(name) is Type type ? services.GetService(type) : null;
    }

    /// <summary>A type by name, from whatever the server has loaded.</summary>
    private static Type? Find(string name)
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Select(one => one.GetType(name, throwOnError: false))
            .FirstOrDefault(one => one is not null);
    }

    /// <summary>A property off an object this plugin cannot name the type of.</summary>
    private static T? Read<T>(object from, string name)
    {
        return from.GetType().GetProperty(name)?.GetValue(from) is T value ? value : default;
    }
}
