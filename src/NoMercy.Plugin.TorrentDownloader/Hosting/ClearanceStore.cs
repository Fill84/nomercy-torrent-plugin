using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// The clearance held for each host, and how it is lost.
/// </summary>
/// <remarks>
/// Per host, because that is how it is issued. Kept in memory only: a clearance
/// is short-lived and tied to a user agent and often to a TLS session, so one
/// read back from disk after a restart is a 403 waiting to happen — and a 403
/// that reads like the site changing its mind.
/// </remarks>
public sealed class ClearanceStore
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, Clearance> _held = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The clearance held for <paramref name="host"/>, if any.</summary>
    public Clearance? For(string host)
    {
        lock (_lock)
        {
            return _held.GetValueOrDefault(host);
        }
    }

    /// <summary>Keeps a fresh clearance.</summary>
    public void Keep(string host, Clearance clearance)
    {
        lock (_lock)
        {
            _held[host] = clearance;
        }
    }

    /// <summary>
    /// Throws away the clearance for <paramref name="host"/>.
    /// </summary>
    /// <remarks>
    /// Spent on refusal rather than trusted until it expires. Clearance is
    /// invalidated for reasons no client can see coming, so the only honest
    /// signal that it has gone is the refusal itself — and holding on to one
    /// that has stopped working turns every subsequent request into a 403 that
    /// looks like the site.
    /// </remarks>
    public void Spend(string host)
    {
        lock (_lock)
        {
            _held.Remove(host);
        }
    }

    /// <summary>How many hosts are currently cleared. For the dashboard.</summary>
    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _held.Count;
            }
        }
    }
}
