using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The torrent client, standing still so the chain can be watched.
/// </summary>
/// <remarks>
/// It decides nothing: it records what it was handed and answers what it was
/// told. Everything in the pipeline that judges anything is the real thing —
/// <strong>H1</strong> — and this stands in only for the sockets at the far end
/// of it.
/// </remarks>
public sealed class FakeTorrentEngine : ITorrentEngine
{
    private readonly Dictionary<string, TorrentState> _states = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request it was handed, in order.</summary>
    public List<TorrentRequest> Taken { get; } = [];

    public Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        Taken.Add(request);

        string hash = Hash(request.Source);
        _states[hash] = TorrentState.FetchingMetadata;

        return Task.FromResult(new TorrentHandle(hash, null));
    }

    public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<TorrentStatus>>(
        [
            .. _states.Select(one => new TorrentStatus(
                one.Key,
                null,
                one.Value,
                BytesDone: 0,
                BytesTotal: null,
                DownloadRateBytesPerSecond: 0,
                UploadRateBytesPerSecond: 0,
                Peers: 0,
                Seeds: 0,
                Ratio: null,
                Eta: null,
                Error: null)),
        ]);
    }

    public Task PauseAsync(string infoHash, CancellationToken ct)
    {
        _states[infoHash] = TorrentState.Paused;

        return Task.CompletedTask;
    }

    public Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        _states[infoHash] = TorrentState.FetchingMetadata;

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        _states.Remove(infoHash);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<TorrentFile>>([]);
    }

    /// <summary>
    /// The hash out of the magnet it was given, or a stand-in.
    /// </summary>
    /// <remarks>
    /// Read rather than invented, so a test that hands over the same torrent
    /// twice sees one hash — which is what the real client does with it.
    /// </remarks>
    private static string Hash(string source)
    {
        int at = source.IndexOf("btih:", StringComparison.OrdinalIgnoreCase);

        return at < 0
            ? "0000000000000000000000000000000000000000"
            : source[(at + 5)..].Split('&')[0].ToUpperInvariant();
    }
}
