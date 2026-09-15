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
    /// <summary>What the site is really sent, as a person would read it.</summary>
    /// <remarks>
    /// Shown on the dashboard as it went out: the exact name with its dots, or
    /// the words a site was given instead. Anything else would be a page
    /// describing a question nobody asked.
    /// </remarks>
    public string AsAsked => Exact ? Text : Query.Sanitised(Text);
}
