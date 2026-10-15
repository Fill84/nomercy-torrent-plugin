using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Pipeline;

/// <summary>An episode's encode in the library: the folder the encoder wrote it into, and the file the library names.</summary>
/// <param name="Folder">The episode's own folder, fully qualified, with forward slashes.</param>
/// <param name="File">The file the library registered for it, inside <paramref name="Folder"/>.</param>
public sealed record EncodedEpisode(string Folder, string File);

/// <summary>Where an episode's encode is, or why that cannot be said.</summary>
/// <param name="Found">The encode, or null.</param>
/// <param name="Why">Why nothing was found, in words for the owner; null when it was.</param>
public sealed record EncodedLookup(EncodedEpisode? Found, string? Why);

/// <summary>
/// Which of the plugin's deliveries an episode is to be replaced over.
/// </summary>
/// <remarks>
/// The owner's rule of 15 October 2026: a show's episode the plugin delivered from a release no name source
/// names is replaced on its own, with nothing for the owner to do.
/// </remarks>
public static class Replacement
{
    /// <summary>The delivery an episode is to be replaced over, or null when there is none.</summary>
    /// <remarks>
    /// <para>
    /// The latest of the plugin's done grabs covering the episode decides, because that is the one whose
    /// encode the library holds: once a replacement is done, it is the latest, its release is named, and the
    /// episode is not marked again though the old grab is still in the store.
    /// </para>
    /// <para>
    /// Only a release known not to be named marks it. A grab from before this was written down is checked
    /// against the name sources first, and until then nothing is known against it. A torrent the owner added
    /// by hand is their own choice of release and is never marked. An episode the owner put in the library
    /// has no grab of the plugin's at all, so there is nothing of the plugin's to replace.
    /// </para>
    /// </remarks>
    public static Replacing? Marked(IReadOnlyList<StoredDownload>? delivered, EpisodeKey episode)
    {
        StoredDownload? last = LastDelivery(delivered, episode);

        return last is { Named: false, ByHand: false } ? new(last.InfoHash, last.ReleaseTitle) : null;
    }

    /// <summary>Where the library holds an episode's encode, or why that cannot be said.</summary>
    /// <param name="episode">The episode.</param>
    /// <param name="showFolder">The show's folder as the library names it, such as <c>/South.Park.(1997)</c>.</param>
    /// <param name="libraryFiles">Every file the library holds for the show, as it answers them.</param>
    /// <remarks>
    /// <para>
    /// <c>VideoEncodeJob</c> writes an episode into <c>{library folder}/{Show.(Year)}/{Show.SxxEyy}/</c> and
    /// registers the master playlist it writes there, named for the episode. That folder is the encode, all of
    /// it, and nothing else is: so the folder is the one the registered file sits in, and it has to be named
    /// for this episode and sit in the show's own folder. Never the show's folder, never another episode's.
    /// </para>
    /// <para>
    /// <strong>The path names the folders twice.</strong> The library answers a file as
    /// <c>HostFolder + Folder + Filename</c>, and the encoder's rows carry a <c>HostFolder</c> that already ends
    /// in <c>/{Show.(Year)}/{Show.SxxEyy}</c> — the same two folders <c>Folder</c> holds. Read on the owner's
    /// server on 15 October 2026. Where the path ends in those two twice, they are the folder once.
    /// </para>
    /// <para>
    /// <strong>Only a fully qualified path.</strong> The older rows carry a <c>HostFolder</c> relative to the
    /// library folder, which names no place on a disk; the server's recycle area refuses anything that is not
    /// under a library folder's own path. Two different folders for one episode are refused too: which of them
    /// the plugin delivered cannot be told.
    /// </para>
    /// </remarks>
    public static EncodedLookup EncodedOf(EpisodeKey episode, string? showFolder, IReadOnlyList<string> libraryFiles)
    {
        string? show = showFolder?.Replace('\\', '/').Trim('/').Split('/')[^1];

        if (string.IsNullOrWhiteSpace(show))
        {
            return new(null, "the library names no folder for its show");
        }

        List<EncodedEpisode> found = [];

        foreach (string path in libraryFiles)
        {
            List<string> parts = [.. path.Replace('\\', '/').Split('/')];

            if (parts.Count < 3 || !Landed.Wrote(episode, [parts[^1]]))
            {
                continue;
            }

            string file = parts[^1];
            parts.RemoveAt(parts.Count - 1);

            if (parts.Count >= 4
                && string.Equals(parts[^4], parts[^2], StringComparison.OrdinalIgnoreCase)
                && string.Equals(parts[^3], parts[^1], StringComparison.OrdinalIgnoreCase))
            {
                parts.RemoveRange(parts.Count - 2, 2);
            }

            string folder = string.Join('/', parts);

            if (parts.Count >= 3
                && Landed.Wrote(episode, [parts[^1]])
                && string.Equals(parts[^2], show, StringComparison.OrdinalIgnoreCase)
                && FullyQualified(folder))
            {
                found.Add(new(folder, $"{folder}/{file}"));
            }
        }

        EncodedEpisode[] folders = [.. found.DistinctBy(one => one.Folder, StringComparer.OrdinalIgnoreCase)];

        return folders.Length switch
        {
            1 => new(folders[0], null),
            0 => new(null, $"the library holds no file of {episode} in a folder of its own inside {show}"),
            _ => new(null, $"the library holds {episode} in more than one folder, so which one the plugin delivered cannot be told"),
        };
    }

    /// <summary>A path that names a place on a disk wherever it is read: a drive, a share or the root.</summary>
    /// <remarks>
    /// Read from the text, not from the platform the plugin runs on: the server answers with the paths of its
    /// own disks, and whether one is relative is a fact about the path.
    /// </remarks>
    private static bool FullyQualified(string path)
    {
        return path.StartsWith('/')
               || (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/');
    }

    /// <summary>The latest of the plugin's done grabs that covers the episode, or null when none does.</summary>
    public static StoredDownload? LastDelivery(IReadOnlyList<StoredDownload>? delivered, EpisodeKey episode)
    {
        return (delivered ?? [])
            .Where(one => one.State == GrabState.Done && one.Covers.Contains(episode))
            .MaxBy(one => one.GrabbedAt);
    }
}
