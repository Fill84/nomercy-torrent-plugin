// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

namespace NoMercy.Plugin.TorrentDownloader.Core.Torrents;

/// <summary>
/// Whether a torrent is worth putting on somebody's disk, judged from its file list.
///
/// <para>
/// A torrent's name says what it claims to be; its files say what it is. On a real server a
/// release named "Lucky 2026 S01E06 1080p WEB h264-ETHEL" turned out to be one 1.2 GB file
/// called <c>.scr</c> - a Windows executable padded out to look like an episode, which is
/// the oldest trick on a public tracker. The plugin wrote it to disk, marked executable,
/// because nothing between the release name and the file system ever looked.
/// </para>
///
/// <para>
/// The import already refused it: only video extensions are moved into the intake, so it
/// would never have reached the library. That is the wrong place to find out. By then a
/// gigabyte of somebody else's program is on the owner's machine.
/// </para>
///
/// <para>
/// Judged from the metadata, which arrives before any piece is requested, so a refusal
/// costs nothing and downloads nothing.
/// </para>
/// </summary>
public static class TorrentContents
{
    /// <summary>
    /// What this plugin exists to fetch.
    ///
    /// <para>
    /// The video is the whole point, and a torrent with none of it is not the episode that
    /// was searched for whatever it calls itself.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".mpg", ".mpeg", ".webm", ".flv", ".m2ts",
    };

    /// <summary>
    /// The inert things a release ships beside its video, and nothing else.
    ///
    /// <para>
    /// Refusing these would refuse almost every real release: a scene torrent ships an nfo,
    /// a checksum, usually a subtitle and often a screenshot. None of them can do anything
    /// on their own.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> CompanionExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".sup",
        ".nfo", ".txt", ".md5", ".sfv", ".sha1", ".jpg", ".jpeg", ".png", ".webp",
    };

    /// <summary>
    /// Things that run, named only so the refusal can say which kind of wrong this is.
    ///
    /// <para>
    /// It decides nothing. The decision is the allowlist above: anything not on it is
    /// refused whether or not it appears here, which is the whole point of an allowlist and
    /// the reason this list not being exhaustive does not matter. A blocklist can only ever
    /// name the dangerous things somebody already thought of, and the next extension nobody
    /// listed walks straight past it.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> ExecutableExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".scr", ".com", ".bat", ".cmd", ".msi", ".msp", ".ps1", ".vbs", ".vbe", ".js", ".jse",
        ".wsf", ".wsh", ".hta", ".jar", ".apk", ".dll", ".cpl", ".lnk", ".pif", ".reg", ".sh", ".run",
    };

    /// <summary>
    /// Why a torrent was refused, or null when it was not.
    ///
    /// <para>
    /// A sentence rather than an enum: it goes straight into the history entry the owner
    /// reads, and there is nothing else in the system that has to branch on which of the
    /// two reasons it was.
    /// </para>
    /// </summary>
    public static string? Refuse(TorrentMetadata metadata)
    {
        IReadOnlyList<FileEntry> files = metadata.Files;

        if (files.Count == 0)
            return "the torrent lists no files at all";

        // The allowlist decides. Anything not on it is refused, whether or not anybody
        // thought to put it on a list of dangerous things - which is the whole reason this
        // is the way round it is. The first version asked "is this an executable", and a
        // question like that is only ever as good as the answers somebody remembered.
        FileEntry? unwanted = files.FirstOrDefault(file => !IsAllowed(Extension(file)));

        if (unwanted is not null)
        {
            string extension = Extension(unwanted);

            // Named differently because they mean different things to whoever reads it: a
            // torrent carrying a program is somebody trying something, and a torrent of
            // archives is a release this plugin simply cannot use.
            return ExecutableExtensions.Contains(extension)
                ? $"it contains a program ({Name(unwanted)}), not just video - refused before anything was written to disk"
                : $"it contains {Name(unwanted)}, which is not video or anything that ships with it - refused before anything was written to disk";
        }

        if (!files.Any(file => VideoExtensions.Contains(Extension(file))))
            return "it contains no video file";

        return null;
    }

    /// <summary>Video, or one of the inert things that ship beside it. Nothing else, ever.</summary>
    private static bool IsAllowed(string extension) =>
        VideoExtensions.Contains(extension) || CompanionExtensions.Contains(extension);

    /// <summary>The file's own name, which is the last of the path segments a torrent carries.</summary>
    private static string Name(FileEntry file) => file.Path.Count == 0 ? string.Empty : file.Path[^1];

    /// <summary>
    /// The extension of the file's own name.
    ///
    /// <para>
    /// Read off the last segment rather than through <c>Path.GetExtension</c> over a joined
    /// path: a torrent's segments are written by whoever made it, on whatever system, and
    /// may carry separators this one does not use.
    /// </para>
    /// </summary>
    private static string Extension(FileEntry file)
    {
        string name = Name(file);
        int dot = name.LastIndexOf('.');

        return dot < 0 ? string.Empty : name[dot..];
    }
}

/// <summary>Thrown when a torrent's contents are not what this plugin will put on a disk.</summary>
public sealed class TorrentContentException(string reason) : Exception(reason);
