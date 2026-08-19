using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// A torrent client that holds exactly what a test tells it to.
/// </summary>
/// <remarks>
/// The cadence's job is to act on what the client says, so what the client says
/// is the input. Nothing here decides anything: every rule under test is in the
/// cadence, and this stands in only for the sockets at the far end of it.
/// </remarks>
public sealed class StandingEngine : ITorrentEngine
{
    private readonly Dictionary<string, TorrentStatus> _holding = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, IReadOnlyList<TorrentFile>> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every request it was handed, in order.</summary>
    public List<TorrentRequest> Taken { get; } = [];

    /// <summary>Every hash it was told to forget, and whether the files went too.</summary>
    public List<(string InfoHash, bool DeleteFiles)> Removed { get; } = [];

    /// <summary>Every hash it was told to pause.</summary>
    public List<string> Paused { get; } = [];

    /// <summary>Every hash it was told to resume.</summary>
    public List<string> Resumed { get; } = [];

    /// <summary>Says it is holding this torrent, in this state.</summary>
    public StandingEngine Holding(TorrentStatus status, params TorrentFile[] files)
    {
        _holding[status.InfoHash] = status;
        _files[status.InfoHash] = files;

        return this;
    }

    public Task<TorrentHandle> AddAsync(TorrentRequest request, CancellationToken ct)
    {
        Taken.Add(request);

        return Task.FromResult(new TorrentHandle("0000000000000000000000000000000000000000", null));
    }

    public Task<IReadOnlyList<TorrentStatus>> StatusAsync(CancellationToken ct)
    {
        return Task.FromResult<IReadOnlyList<TorrentStatus>>([.. _holding.Values]);
    }

    public Task PauseAsync(string infoHash, CancellationToken ct)
    {
        Paused.Add(infoHash);

        return Task.CompletedTask;
    }

    public Task ResumeAsync(string infoHash, CancellationToken ct)
    {
        Resumed.Add(infoHash);

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string infoHash, bool deleteFiles, CancellationToken ct)
    {
        Removed.Add((infoHash, deleteFiles));
        _holding.Remove(infoHash);

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TorrentFile>> FilesAsync(string infoHash, CancellationToken ct)
    {
        return Task.FromResult(_files.GetValueOrDefault(infoHash, []));
    }
}
