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
    /// list, no subscription and no opt-in either: this hands back every show
    /// in those libraries that has somewhere to download to, and whether one is
    /// a show the owner actually has is <c>Ownership.Theirs</c>'s question, not
    /// this port's.
    /// </remarks>
    Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct);

    /// <summary>Every library this plugin is for, of both kinds.</summary>
    /// <remarks>
    /// The shows are enough for everything the search chain does, because a
    /// show carries the library it is in. This is for the one case where there
    /// is no show to ask: a torrent added by hand whose files name a series the
    /// server has never heard of. Somewhere has to be named for it, and until
    /// this was here the plugin could not even ask which places exist.
    /// </remarks>
    Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken ct);

    /// <summary>
    /// Every episode of one show, including the ones with no file — those are
    /// the gaps this plugin exists to fill.
    /// </summary>
    Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct);

}
