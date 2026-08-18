using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Naming;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>One file to move, and which episode it is.</summary>
/// <param name="Path">Its path inside the torrent.</param>
/// <param name="Episode">Which episode it answers for.</param>
/// <param name="Length">How big it is.</param>
public sealed record Staged(string Path, EpisodeKey Episode, long Length);

/// <summary>
/// Which files out of a finished torrent belong in the library.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Only video files are written into a library folder.</strong> That is
/// a rule from the working agreement rather than a preference: everything else
/// in a torrent — the subtitles, the NFO, the screenshot folder, the text file
/// pointing at a website — is somebody else's idea of what belongs on the
/// owner's disk, and some of it is worse than clutter.
/// </para>
/// <para>
/// It decides and moves nothing. What it answers is a list of files and the
/// episodes they are for, which is testable without a disk.
/// </para>
/// </remarks>
public static class Staging
{
    /// <summary>
    /// What counts as video.
    /// </summary>
    /// <remarks>
    /// The same list the release parser uses to strip an extension off a name,
    /// so a file this plugin can recognise in a release title is a file it will
    /// also stage.
    /// </remarks>
    public static IReadOnlySet<string> VideoExtensions { get; } =
        new HashSet<string>([".mkv", ".mp4", ".avi", ".iso", ".ts", ".m4v", ".wmv", ".mov"], StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// How small a video has to be before it is taken for a sample.
    /// </summary>
    /// <remarks>
    /// Fifty megabytes. A sample is a minute of the film and is the one thing
    /// in a torrent that is a real video file and must never be staged — an
    /// episode replaced by its own sample looks like a successful download
    /// until somebody presses play.
    /// </remarks>
    public const long SampleUnder = 50L * 1024 * 1024;

    /// <summary>
    /// Which files answer for which episodes.
    /// </summary>
    /// <param name="files">Everything in the finished torrent.</param>
    /// <param name="covers">
    /// Every episode the grab was for. One for an ordinary release, several for
    /// a season pack.
    /// </param>
    public static IReadOnlyList<Staged> Choose(IReadOnlyList<TorrentFile> files, IReadOnlyList<EpisodeKey> covers)
    {
        TorrentFile[] all =
        [
            .. files
                .Where(one => VideoExtensions.Contains(System.IO.Path.GetExtension(one.Path)))
                .OrderByDescending(one => one.Length),
        ];

        // Size only says "sample" when there is something bigger for it to be a
        // sample of. A twenty-minute anime episode at a low bitrate is smaller
        // than this and is the whole torrent — refusing it would leave the
        // episode missing for ever, and a mutation showed no test would have
        // noticed.
        bool bigger = all.Any(one => one.Length >= SampleUnder);

        TorrentFile[] videos = [.. all.Where(one => !Sample(one, bigger))];

        if (videos.Length == 0 || covers.Count == 0)
        {
            return [];
        }

        if (covers.Count == 1)
        {
            // The largest video, and nothing else. A release for one episode
            // that contains three videos has two the owner did not ask for.
            return [new(videos[0].Path, covers[0], videos[0].Length)];
        }

        List<Staged> staged = [];

        foreach (EpisodeKey episode in covers)
        {
            // A pack is matched by episode number in the file's own name, never
            // by order: a torrent lists its files however it likes, and staging
            // episode four as episode one is worse than staging nothing.
            TorrentFile? match = videos.FirstOrDefault(one => Answers(one, episode));

            if (match is not null)
            {
                staged.Add(new(match.Path, episode, match.Length));
            }
        }

        return staged;
    }

    /// <summary>Every episode the grab covered that no file answered for.</summary>
    /// <remarks>
    /// A pack that is missing an episode is worth saying so about: the episode
    /// stays missing and is looked for again, rather than being marked as
    /// having arrived because the pack did.
    /// </remarks>
    public static IReadOnlyList<EpisodeKey> Unanswered(IReadOnlyList<Staged> staged, IReadOnlyList<EpisodeKey> covers)
    {
        return [.. covers.Where(one => !staged.Any(file => file.Episode == one))];
    }

    /// <summary>Whether this file is the one for that episode.</summary>
    private static bool Answers(TorrentFile file, EpisodeKey episode)
    {
        ReleaseName name = ReleaseName.Parse(System.IO.Path.GetFileName(file.Path));

        if (name.Season is int season && season != episode.Season)
        {
            return false;
        }

        return name.Episode == episode.Number
               || (name.Episode is int first && name.LastEpisode is int last
                   && episode.Number >= first && episode.Number <= last);
    }

    /// <summary>
    /// Whether this is a sample rather than the thing itself.
    /// </summary>
    /// <remarks>
    /// By name always, and by size only when the torrent holds something bigger
    /// for it to be a sample of. Both happen — a file in a folder called
    /// <c>Sample</c>, and a two-minute clip beside a three-gigabyte episode —
    /// but a small video that is the only video is the episode.
    /// </remarks>
    private static bool Sample(TorrentFile file, bool somethingBigger)
    {
        return file.Path.Contains("sample", StringComparison.OrdinalIgnoreCase)
               || (somethingBigger && file.Length < SampleUnder);
    }
}
