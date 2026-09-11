using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// One question put to an indexer, and the form it goes out in.
/// </summary>
/// <param name="Text">What is asked.</param>
/// <param name="Exact">
/// Letter for letter, only made safe for an address. False is the site's own
/// style, which turns punctuation into spaces.
/// </param>
public sealed record SearchTerm(string Text, bool Exact)
{
    /// <summary>
    /// The whole ladder one indexer climbs: every release name the sources
    /// gave, exactly and then without its punctuation, and only then the
    /// questions this plugin makes up.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>The owner's rule of 11 September 2026.</strong> A source is
    /// asked what the episode was released as so that the indexers can be
    /// asked for exactly that — <c>Dark.Matter.2024.S02E03.1080p.WEB.H264-CAKES</c>,
    /// dots and dash and all. Every name used to go out as loose words, so the
    /// question the source had answered was never put to anybody.
    /// </para>
    /// <para>
    /// No rows to the exact name, and the same name is asked without its
    /// punctuation: some sites tokenise on spaces and find nothing in a
    /// string of dots. No rows to that either, and only then the ladder.
    /// </para>
    /// </remarks>
    /// <param name="releaseNames">The names the sources gave that the profile accepts.</param>
    /// <param name="rungs">The questions made up here, narrowest first.</param>
    public static IReadOnlyList<SearchTerm> Ladder(IEnumerable<string> releaseNames, IEnumerable<string> rungs)
    {
        return
        [
            .. releaseNames.SelectMany(name => (SearchTerm[])[new(name, true), new(name, false)]),
            .. rungs.Select(rung => new SearchTerm(rung, false)),
        ];
    }

    /// <summary>What the site is really sent, as a person would read it.</summary>
    /// <remarks>
    /// Shown on the dashboard as it went out: the exact name with its dots, or
    /// the words a site was given instead. Anything else would be a page
    /// describing a question nobody asked.
    /// </remarks>
    public string AsAsked => Exact ? Text : Query.Sanitised(Text);
}
