// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using System.Text.RegularExpressions;

namespace NoMercy.Plugin.TorrentDownloader.Core.Library;

/// <summary>Which show a typed name turned out to mean.</summary>
public enum ShowLookup
{
    /// <summary>Exactly one. <see cref="ShowMatch.Show"/> is it.</summary>
    One,

    /// <summary>Nothing in the library is called that.</summary>
    None,

    /// <summary>More than one, and the plugin will not pick for the owner. <see cref="ShowMatch.Candidates"/> names them.</summary>
    Several,
}

/// <param name="Candidates">Named with their years, because the ambiguity that actually happens is two shows with the same title.</param>
public sealed record ShowMatch(ShowLookup Outcome, LibraryShow? Show, IReadOnlyList<string> Candidates)
{
    public static ShowMatch None { get; } = new(ShowLookup.None, null, []);
}

/// <summary>
/// Finding one show in the library by what somebody typed.
///
/// <para>
/// The plugin only ever works on shows with an episode on the server, which is right and
/// which also means it can never start a new one. This is the way round that: the owner
/// names a show, and naming it is the whole permission. So the matching has to be
/// forgiving enough to be usable and strict enough never to follow a show nobody meant -
/// which is why more than one match is an answer rather than a coin toss.
/// </para>
///
/// <para>
/// Pure, and here rather than in the shell, so every rule below is a test that needs no
/// server.
/// </para>
/// </summary>
public static class LibraryShowFinder
{
    /// <summary>A year the owner typed to tell two shows of the same name apart, with or without brackets.</summary>
    private static readonly Regex TrailingYear = new(@"[\s(\[]*\b(19|20)\d{2}\b[)\]]*\s*$", RegexOptions.Compiled);

    public static ShowMatch Find(IReadOnlyList<LibraryShow> shows, string? typed)
    {
        string text = (typed ?? string.Empty).Trim();

        if (text.Length == 0)
            return ShowMatch.None;

        int? year = YearIn(text);
        string name = year is null ? text : TrailingYear.Replace(text, string.Empty).Trim();

        if (name.Length == 0)
            return ShowMatch.None;

        List<LibraryShow> pool = year is null
            ? [.. shows]
            : [.. shows.Where(show => show.Year == year)];

        // Exact first. "Lucky" and "Lucky Luke" are both real shows on a real server, and
        // without this pass typing the shorter one can only ever be ambiguous.
        List<LibraryShow> exact = [.. pool.Where(show => Same(show.Title, name))];

        List<LibraryShow> matches = exact.Count > 0
            ? exact
            : [.. pool.Where(show => show.Title.Contains(name, StringComparison.CurrentCultureIgnoreCase))];

        return matches switch
        {
            [] => ShowMatch.None,
            [LibraryShow only] => new ShowMatch(ShowLookup.One, only, []),
            _ => new ShowMatch(ShowLookup.Several, null, [.. matches.Select(Describe).Order(StringComparer.CurrentCultureIgnoreCase)]),
        };
    }

    private static bool Same(string title, string name) =>
        string.Equals(title.Trim(), name, StringComparison.CurrentCultureIgnoreCase);

    private static string Describe(LibraryShow show) =>
        show.Year is int year ? $"{show.Title} ({year})" : show.Title;

    private static int? YearIn(string text)
    {
        Match match = TrailingYear.Match(text);

        // Only when something precedes it. A bare "2026" is a title as far as this is
        // concerned - there are shows called that - and stripping it would leave nothing
        // to search for.
        if (!match.Success || match.Index == 0)
            return null;

        return int.Parse(Regex.Match(match.Value, @"(19|20)\d{2}").Value);
    }
}
