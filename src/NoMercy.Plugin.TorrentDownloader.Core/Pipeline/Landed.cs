using System.Text.RegularExpressions;

using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>
/// Whether the encode of an episode really arrived, when the server says the
/// job is over and the library still shows a gap.
/// </summary>
/// <remarks>
/// <para>
/// The library having the episode is the ordinary proof and the only one worth
/// trusting while a job is still running. This is for the case after it: the
/// server says the encode finished, and the episode still has no file.
/// </para>
/// <para>
/// <strong>It happened, and it is the server's own registration that is
/// wrong.</strong> On 1 September 2026 the plugin dispatched South Park S15E12
/// with the server's own id for it, <c>153823</c>; the encoder logged
/// <c>for 153823</c>, wrote
/// <c>/South.Park.(1997)/South.Park.S15E12/South.Park.S15E12.1%.NoMercy.m3u8</c>,
/// and the post-encode registration attached that file to episode
/// <c>153785</c> — season 0, "Chef Aid: Behind The Menu". Twice. So the real
/// S15E12 had no file, the queue was empty, and the plugin sat on "encoding"
/// for six hours before giving up and downloading the same episode again.
/// </para>
/// <para>
/// <strong>The file's own name is the answer.</strong> The encoder names what
/// it writes after the episode it was asked for, so a file in the show's
/// folders whose name carries this season and episode is this episode, whatever
/// row the server attached it to. That is a fact about the disk, not a guess.
/// </para>
/// <para>
/// <strong>Only once the job is over.</strong> Asked while an encode is still
/// running this would read a file being written as a file that arrived, and the
/// caller deletes the download on the strength of it — which is the fault that
/// cost the owner 36 GB. The caller asks the server first and comes here only
/// for a job it has been told is finished.
/// </para>
/// </remarks>
public static partial class Landed
{
    /// <summary>
    /// Whether a file in <paramref name="paths"/> is named for
    /// <paramref name="wanted"/>.
    /// </summary>
    /// <remarks>
    /// The whole path, not only the file name: the encoder writes an episode
    /// into a folder named for it as well, so a format that leaves the numbers
    /// off one still carries them on the other.
    /// </remarks>
    public static bool Wrote(EpisodeKey wanted, IReadOnlyList<string> paths)
    {
        foreach (string path in paths)
        {
            foreach (Match numbered in Numbered().Matches(path))
            {
                if (int.Parse(numbered.Groups[1].ValueSpan) == wanted.Season
                    && int.Parse(numbered.Groups[2].ValueSpan) == wanted.Number)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>A season and an episode as every library file spells them.</summary>
    /// <remarks>
    /// Three digits for the episode, because a long-running show has them, and
    /// two for the season, because none has a hundred.
    /// </remarks>
    [GeneratedRegex(@"[Ss](\d{1,2})[Ee](\d{1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex Numbered();
}
