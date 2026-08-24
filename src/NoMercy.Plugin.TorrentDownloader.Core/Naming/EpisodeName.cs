using System.Globalization;
using System.Text.RegularExpressions;
using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Naming;

/// <summary>
/// What an episode is called once this plugin has it.
/// </summary>
/// <remarks>
/// <para>
/// The show, its year, the episode and the quality — nothing else. Two
/// releases of one episode at one quality therefore come to the same name and
/// the same path, so a second copy of an episode cannot exist: there is
/// nowhere for it to be.
/// </para>
/// <para>
/// It used to be the release title, which is the uploader's text off a web
/// page. The owner's intake folder held ten files for five episodes, in pairs
/// differing only by the site's tag on the end, because two rows of one
/// torrent carried the indexer's spelling and the plugin's. Naming from the
/// episode rather than from the release is what makes that impossible rather
/// than merely fixed.
/// </para>
/// <para>
/// Written in the shape a release is written, with dots for spaces, because
/// the media server parses this name to work out what the file is and that is
/// the shape its parser was built against.
/// </para>
/// </remarks>
public static class EpisodeName
{
    /// <summary>Whitespace, however much of it and whatever kind.</summary>
    private static readonly Regex Spacing = new(
        @"\s+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Dots that ran together where something was dropped between them.</summary>
    private static readonly Regex Runs = new(
        @"\.{2,}",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>The name for one episode of one show at one quality.</summary>
    /// <param name="showTitle">The show as the library has it.</param>
    /// <param name="year">The year the show began, when it is known.</param>
    /// <param name="episode">Which episode.</param>
    /// <param name="resolution">As the ladder writes it: <c>1080p</c>.</param>
    /// <param name="extension">Taken from the file, with its dot.</param>
    public static string For(
        string showTitle,
        int? year,
        EpisodeKey episode,
        string? resolution,
        string extension)
    {
        List<string> parts = [Dotted(showTitle)];

        // Only what is known. A name carrying "unknown" or a year of 0 would be
        // written into the owner's folder and read back by the server as part
        // of the show's title.
        if (year is int began)
        {
            parts.Add(began.ToString(CultureInfo.InvariantCulture));
        }

        // Two digits is the shape every release uses. A longer number keeps its
        // own length rather than being cut to fit it.
        parts.Add(string.Create(
            CultureInfo.InvariantCulture,
            $"S{episode.Season:D2}E{episode.Number:D2}"));

        if (!string.IsNullOrWhiteSpace(resolution))
        {
            parts.Add(Dotted(resolution));
        }

        return string.Join('.', parts.Where(one => one.Length > 0)) + extension;
    }

    /// <summary>A title as a file name: spaces to dots, the rest dropped.</summary>
    /// <remarks>
    /// Dropped rather than replaced, because a character standing in for a
    /// colon becomes part of the title when the server parses the name back.
    /// </remarks>
    private static string Dotted(string title)
    {
        string spaced = Spacing.Replace(title.Trim(), ".");

        string kept = string.Concat(spaced.Where(one =>
            one == '.' || (!Path.GetInvalidFileNameChars().Contains(one) && one != '\'')));

        return Runs.Replace(kept, ".").Trim('.');
    }
}
