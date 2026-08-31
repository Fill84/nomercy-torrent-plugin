using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The library, asked each question once and then remembered for the rest of
/// one tick.
/// </summary>
/// <remarks>
/// <para>
/// The transfers cadence runs every minute and asks the same things several
/// times inside it: the shows once per staged file and once per dispatch, a
/// show's files once per dispatch, and a show's episodes from two places that
/// each kept a cache of their own. A tick staging four episodes made eight
/// round trips for a list that cannot change while the tick is running.
/// </para>
/// <para>
/// <strong>One of these lives for one tick and is then thrown away.</strong>
/// That is the whole of its correctness: the library does change — the server
/// encodes, and an episode gains a file — and an answer kept beyond the tick
/// that asked for it would be the plugin deciding on what used to be true. A
/// tick lasts moments; the next one asks again.
/// </para>
/// <para>
/// It is not shared between ticks and not shared between threads, so nothing
/// here locks. A tick is one pass of one loop.
/// </para>
/// </remarks>
public sealed class LibraryThisTick(ILibrary library) : ILibrary
{
    private readonly Dictionary<int, IReadOnlyList<Episode>> _episodes = [];

    private readonly Dictionary<int, IReadOnlyList<string>> _files = [];

    private IReadOnlyList<Show>? _shows;

    private IReadOnlyList<Library>? _libraries;

    public async Task<IReadOnlyList<Library>> GetLibrariesAsync(CancellationToken ct)
    {
        return _libraries ??= await library.GetLibrariesAsync(ct);
    }

    public async Task<IReadOnlyList<Show>> GetShowsAsync(CancellationToken ct)
    {
        return _shows ??= await library.GetShowsAsync(ct);
    }

    public async Task<IReadOnlyList<Episode>> GetEpisodesAsync(int showId, CancellationToken ct)
    {
        if (_episodes.TryGetValue(showId, out IReadOnlyList<Episode>? known))
        {
            return known;
        }

        IReadOnlyList<Episode> episodes = await library.GetEpisodesAsync(showId, ct);

        _episodes[showId] = episodes;

        return episodes;
    }

}
