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

        // Only the torrent as a whole is judged here. Which of its files reach the disk is
        // decided per file by IsVideo, because refusing a release for shipping an nfo would
        // refuse almost every real one - a scene torrent nearly always carries a checksum,
        // usually a subtitle and often a screenshot.
        if (files.Any(IsVideo))
            return null;

        // Nothing worth having. Named by what it does carry, because "it contains a
        // program" and "it contains no video" mean very different things to whoever reads
        // it: one is somebody trying something, the other is a release this plugin cannot
        // use. This is the case the .scr fell into - the torrent was nothing else.
        FileEntry? program = files.FirstOrDefault(file => ExecutableExtensions.Contains(Extension(file)));

        return program is not null
            ? $"it contains a program ({Name(program)}) and no video at all - refused before anything was written to disk"
            : "it contains no video file";
    }

    /// <summary>
    /// Whether this file is one this plugin will put on a disk.
    ///
    /// <para>
    /// An allowlist of video, and nothing else: not the nfo, not the sample, not the
    /// screenshot, and certainly not whatever extension somebody invents next. Everything
    /// outside it is skipped as the torrent downloads rather than refusing the torrent, so
    /// an ordinary release still arrives - with only its video written.
    /// </para>
    /// </summary>
    public static bool IsVideo(FileEntry file) => VideoExtensions.Contains(Extension(file));

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
