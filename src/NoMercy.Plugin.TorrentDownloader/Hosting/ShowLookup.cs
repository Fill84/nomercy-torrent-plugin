using System.Globalization;
using System.Reflection;

using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>What the server's metadata providers know a show as.</summary>
/// <param name="Title">The title the provider spells it with, which is the one the owner will type.</param>
/// <param name="Year">The year it started, where the provider gives one.</param>
/// <param name="ProviderId">The provider's own id for it, said out loud so two shows of one name can be told apart.</param>
public sealed record FoundShow(string Title, int? Year, int ProviderId);

/// <summary>
/// Asks the server's own metadata providers which show a torrent holds.
/// </summary>
/// <remarks>
/// <para>
/// <strong>It looks up. It does not add.</strong> What stood here added the
/// show: it took the nearest candidate and dispatched
/// <c>ShowImportJob(tmdbId, libraryId)</c>, which is the call the dashboard's
/// <em>Add content</em> makes when a person adds one. Deciding that a show
/// belongs in the owner's library, on a title parsed out of a file name, is not
/// this plugin's decision to make. It fills a library the owner has built; it
/// does not build it.
/// </para>
/// <para>
/// <strong>And handing the files over instead does nothing.</strong> The
/// server's <c>PluginEncoder</c> puts the <c>mediaId</c> straight into
/// <c>VideoEncodeJob.Id</c>, and that job resolves the id against
/// <c>Movies.Id</c> or <c>Episodes.Id</c> and nothing else. A show's id matches
/// neither. No id at all matches neither. The job returns having done no work
/// while the queue records it finished — nine files, nine jobs finished inside
/// two minutes, and nothing written to the library, on 31 August 2026.
/// </para>
/// <para>
/// What is left is worth keeping for one sentence. "It holds episodes of
/// <c>Dark.Matter</c>" is parsed off a file name and may be a mangling of one;
/// "the providers know it as Dark Matter (2024)" is the name the owner types
/// into <em>Add content</em>.
/// </para>
/// <para>
/// <strong>It reaches the server by name, and that is deliberate.</strong> The
/// plugin contract offers no way to ask a provider anything, so there is
/// nothing to call. Every step is guarded and every failure is one line the
/// owner can read, because a server that renames this type will break it — and
/// when it does, the plugin says the show's name the way its files spell it and
/// carries on rather than falling over.
/// </para>
/// </remarks>
public sealed class ShowLookup(IServiceProvider services, ILogger logger)
{
    private const string ProbeType = "NoMercy.MediaProcessing.Inbox.IInboxMetadataProbe";

    /// <summary>
    /// Says whether this server has the probe, before anything needs it.
    /// </summary>
    /// <remarks>
    /// Asked once when the plugin wakes, because otherwise the answer arrives
    /// only when a torrent for an unknown show finishes — which can be an hour
    /// of downloading away, and is the worst moment to find out. It names the
    /// type, so a server that renamed it can be told from one that never had
    /// it.
    /// </remarks>
    public void Ready()
    {
        if (Find(ProbeType) is null)
        {
            logger.LogWarning(
                "A show this owner does not have will be named the way its own files spell it: "
                + "this server offers no {Missing} to look it up with.",
                ProbeType);

            return;
        }

        if (Resolve(ProbeType) is null)
        {
            logger.LogWarning(
                "This server has the metadata probe but does not hand it out, so a show the owner "
                + "does not have will be named the way its own files spell it.");

            return;
        }

        logger.LogInformation(
            "A torrent for a show the owner does not have will be named by this server's own "
            + "metadata providers. Adding it stays the owner's to do.");
    }

    /// <summary>
    /// Asks the providers what the show a torrent's files name really is.
    /// </summary>
    /// <param name="title">The show's title, as the release name spells it.</param>
    /// <param name="year">Its year where the release name carries one.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>What they know it as, or null where nothing could be asked or nothing answered.</returns>
    public async Task<FoundShow?> FindAsync(string title, int? year, CancellationToken ct)
    {
        object? probe = Resolve(ProbeType);

        if (probe is null)
        {
            return null;
        }

        object? found = await SearchAsync(probe, title, year, ct).ConfigureAwait(false);

        if (found is null)
        {
            return null;
        }

        string? external = Read<string>(found, "ExternalId");
        string? matched = Read<string>(found, "Title");

        if (matched is null
            || external is null
            || !int.TryParse(external, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
        {
            logger.LogWarning("The provider named {Title} without an id, so it is named as its files spell it.", title);

            return null;
        }

        return new(matched, Read<int?>(found, "Year"), id);
    }

    /// <summary>Asks the server's providers, and takes the nearest answer.</summary>
    /// <remarks>
    /// The year decides where there is one: a title on its own matches remakes
    /// and reboots, and naming the wrong show is worse than naming none — the
    /// owner acts on this line, and the act is adding a show to a library.
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
            logger.LogInformation("No provider knows a show called {Title}.", title);

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
