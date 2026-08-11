// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// Reaching the server's own services, types and members by name.
///
/// <para>
/// The plugin runs inside the server's process, so the container already holds
/// <c>IJobDispatcher</c>, the repositories and the job types. Referencing those assemblies
/// would drag the encoder and the whole EF model into this plugin's ABI, which is the one
/// thing the plugin contract was shaped to avoid - so they are found by name instead.
/// </para>
///
/// <para>
/// Kept apart from the code that uses it. What a dispatch needs to say is "this library,
/// its first folder, this file, this match", and that sentence was buried under two hundred
/// lines of plumbing shared by three call sites.
/// </para>
///
/// <para>
/// Nothing here guesses past a miss. A wrong answer is a job that runs and does nothing,
/// which reads exactly like success - so every failure is reported and answers null.
/// </para>
/// </summary>
internal sealed class HostServices(IServiceProvider services, ILogger logger)
{
    /// <summary>
    /// A method by exact name, then by prefix, taking only ones this can actually call -
    /// every parameter is either the id or a cancellation token, and anything else would be
    /// passed null and mean something nobody here intended.
    /// </summary>
    internal static MethodInfo? Method(object? target, string name) =>
        target?.GetType()
            .GetMethods()
            .Where(method => method.Name.StartsWith(name, StringComparison.Ordinal))
            .Where(method => method.GetParameters().All(parameter =>
                parameter.ParameterType == typeof(Ulid) || parameter.ParameterType == typeof(CancellationToken)))
            .OrderBy(method => method.Name.Length)
            .FirstOrDefault();

    internal static object?[] Arguments(MethodInfo method, Ulid libraryId, CancellationToken ct) =>
        [
            .. method.GetParameters().Select(parameter =>
                parameter.ParameterType == typeof(CancellationToken)
                    ? ct
                    : parameter.ParameterType == typeof(Ulid)
                        ? libraryId
                        : (object?)null),
        ];

    internal static async Task<object?> Unwrap(Task task)
    {
        await task;

        return task.GetType().GetProperty("Result")?.GetValue(task);
    }

    internal object? Resolve(string typeName)
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
    internal static string WhatIsThere()
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

    internal Type? FindType(string fullName)
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

    internal static object? Get(object target, string property) =>
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
    internal bool Set(object target, string property, object? value)
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
