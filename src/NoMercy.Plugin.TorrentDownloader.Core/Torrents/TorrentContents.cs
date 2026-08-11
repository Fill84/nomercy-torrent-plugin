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
    /// What this plugin exists to fetch. An allowlist, because the interesting property is
    /// "known to be video" and not "not one of the bad ones I thought of".
    /// </summary>
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".mpg", ".mpeg", ".webm", ".flv", ".m2ts",
    };

    /// <summary>
    /// Things that run.
    ///
    /// <para>
    /// Separate from "not a video" so the refusal can say which of the two it is, because
    /// they mean very different things to the person reading it: a torrent full of images
    /// is a mistake, and a torrent carrying an executable is somebody trying something.
    /// </para>
    ///
    /// <para>
    /// Deliberately not a setting. Every other rule in this plugin is the owner's to
    /// change; this one is not worth the click that turns it off.
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

        // First, because it is the dangerous one. A torrent that carries both a video and
        // an executable is still refused: there is no reason for one to be in the other's
        // company, and "download the good half" is not a judgement worth making on
        // somebody else's behalf.
        FileEntry? program = files.FirstOrDefault(file => ExecutableExtensions.Contains(Extension(file)));

        if (program is not null)
        {
            return $"it contains a program ({Name(program)}), not just video - "
                + "refused before anything was written to disk";
        }

        if (!files.Any(file => VideoExtensions.Contains(Extension(file))))
            return "it contains no video file";

        return null;
    }

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
