using NoMercy.Plugin.TorrentDownloader.Bittorrent;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// Trackers that answer nothing at all.
/// </summary>
/// <remarks>
/// What the engine is judged on here is its bookkeeping — one torrent per hash,
/// the metadata clock, what pause means — and none of that needs a swarm. A
/// transport is required rather than defaulted precisely so that no test can
/// reach a real tracker by forgetting to give it one.
/// </remarks>
public sealed class SilentTrackers : ITrackerTransport
{
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes the first time a tracker is really asked anything.</summary>
    /// <remarks>
    /// A signal rather than a count, because the announce runs on its own and a
    /// test that read a counter would be reading it before or after the ask
    /// depending on the day.
    /// </remarks>
    public Task Asked => _asked.Task;

    public Task<byte[]> GetAsync(Uri address, CancellationToken ct)
    {
        _asked.TrySetResult();

        throw new HttpRequestException("nothing answered");
    }

    public Task<byte[]> ExchangeAsync(string host, int port, byte[] datagram, TimeSpan patience, CancellationToken ct)
    {
        _asked.TrySetResult();

        throw new TimeoutException($"{host}:{port} did not answer.");
    }
}

/// <summary>A swarm with nobody in it.</summary>
/// <remarks>
/// Every address answers nothing, which is what most of the addresses a real
/// tracker hands out do.
/// </remarks>
public sealed class NoPeers : IPeerDialler
{
    public Task<PeerConnection?> DialAsync(
        PeerAddress peer,
        byte[] infoHash,
        byte[] peerId,
        int pieces,
        CancellationToken ct)
    {
        return Task.FromResult<PeerConnection?>(null);
    }
}
