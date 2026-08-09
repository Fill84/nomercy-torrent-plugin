// SPDX-License-Identifier: MIT
// Copyright (c) 2026 Phillippe Pelzer - https://github.com/Fill84

using Microsoft.Extensions.Logging;
using NoMercy.Events;
using NoMercy.Events.FileWatcher;
using NoMercy.Plugin.TorrentDownloader.Core.Orchestration;
using NoMercy.Plugin.TorrentDownloader.Core.Store;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The last step: the finished download goes to the folder the owner nominated, and the
/// server is told it is there.
///
/// <para>
/// Telling it is the part that has to be deliberate. The server watches the folders that
/// belong to a library and nothing else, so a finished folder outside one is a folder
/// nobody looks at - the episode would sit there complete and never appear. Publishing
/// <see cref="FileCreatedEvent"/> is how a plugin says "here, this belongs to that
/// library": the server's own handler scans the folder, looks the show up, and dispatches
/// the import job for that library with that library's settings, encoding preset
/// included. Which is where that decision belongs - the plugin has no business knowing
/// how the owner wants their media encoded.
/// </para>
///
/// <para>
/// It needs the library, not just the file, which is why this lives in the shell rather
/// than in Core: the event type and the library query are both the host's.
/// </para>
/// </summary>
public sealed class LibraryImportHandoff(
    FinishedFolderMover mover,
    IPluginLibraryQuery library,
    IEventBus events,
    ILogger logger
) : IIntakeHandoff
{
    public async Task<bool> MoveIntoIntakeAsync(string completedFolder, EpisodeKey key, CancellationToken ct)
    {
        string? finished = await mover.MoveAsync(completedFolder, ct);

        if (finished is null)
        {
            return false;
        }

        PluginLibraryShow? show = (await library.GetShowsAsync(ct: ct))
            .FirstOrDefault(candidate => candidate.Id == key.ShowId);

        if (show is null || !Ulid.TryParse(show.LibraryId, out Ulid libraryId))
        {
            // The bytes are safe in the finished folder either way. Saying so loudly
            // matters because the alternative failure is silent: a complete episode in a
            // folder nobody is watching, which reads as a download that never happened.
            logger.LogWarning(
                "Torrent Downloader moved {Folder} but could not tell the server which library it belongs to.",
                finished);

            return true;
        }

        string type = await LibraryTypeAsync(show.LibraryId, ct);

        await events.PublishAsync(
            new FileCreatedEvent
            {
                FolderPath = finished,
                LibraryId = libraryId,
                LibraryType = type,
            },
            ct);

        logger.LogInformation(
            "Torrent Downloader handed {Folder} to the {Type} library for import.",
            finished,
            type);

        return true;
    }

    /// <summary>
    /// The library's own type, because the server's handler switches on it and treats tv
    /// and anime differently from a movie. Falling back to "tv" rather than refusing: the
    /// shows this plugin follows come from tv and anime libraries in the first place, so
    /// a type it cannot read is a lookup that failed, not a movie.
    /// </summary>
    private async Task<string> LibraryTypeAsync(string libraryId, CancellationToken ct) =>
        (await library.GetLibrariesAsync(ct))
            .FirstOrDefault(candidate => candidate.Id == libraryId)
            ?.Type
        ?? "tv";
}
