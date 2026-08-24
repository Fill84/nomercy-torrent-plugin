using NoMercy.Plugin.TorrentDownloader.Core.Domain;

namespace NoMercy.Plugin.TorrentDownloader.Core.Ports;

/// <summary>
/// What the pipeline is allowed to know about the media server's library.
/// </summary>
/// <remarks>
/// <para>
/// The media server is the only source of truth about what exists and what is
/// on disk; the plugin derives everything and stores none of it as fact. This
/// port is where that truth arrives, and it is deliberately narrow: two calls,
/// both read-only, neither able to alter anything.
/// </para>
/// <para>
/// It is here rather than in the shell so that the whole pipeline can be judged
/// without a media server. The adapter that fulfils it maps and nothing else,
/// which means everything worth arguing about is on this side of the line.
/// </para>
/// </remarks>
public interface ILibrary
{
    /// <summary>
    /// Every show this plugin has business with: those in television and anime
    /// libraries that have somewhere to download to.
    /// </summary>
    /// <remarks>
    /// Films are not among them and are never asked for. There is no follow
    /// list, no subscription and no opt-in either — every show in those
    /// libraries is in scope.
    /// </remarks>
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct);

    /// <summary>
    /// Every episode of one show, including the ones with no file — those are
    /// the gaps this plugin exists to fill.
    /// </summary>
    Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct);

    /// <summary>
    /// Where this show's episodes already are, as full paths.
    /// </summary>
    /// <remarks>
    /// A library can have more than one folder — the owner's has two, on
    /// different drives — and an encode has to be sent to the one the show
    /// really lives in. Taking the library's first folder sent every episode to
    /// a drive the server could not reach, and every encode failed with
    /// "could not find a part of the path".
    /// </remarks>
    Task<IReadOnlyList<string>> GetShowFilesAsync(int showId, CancellationToken ct);
}
