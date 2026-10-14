namespace NoMercy.Plugin.TorrentDownloader.Bittorrent.Tests.TestSupport;

/// <summary>A log that keeps what it was told, at the levels a test turned on.</summary>
public sealed class RecordingLog(bool debugging = true, bool tracing = true) : IEngineLog
{
    private readonly List<string> _debug = [];
    private readonly List<string> _trace = [];
    private readonly Lock _lock = new();

    public bool Debugging => debugging;

    public bool Tracing => tracing;

    /// <summary>Every Debug line, in the order it was said.</summary>
    public IReadOnlyList<string> Debugged
    {
        get
        {
            lock (_lock)
            {
                return [.. _debug];
            }
        }
    }

    /// <summary>Every Trace line, in the order it was said.</summary>
    public IReadOnlyList<string> Traced
    {
        get
        {
            lock (_lock)
            {
                return [.. _trace];
            }
        }
    }

    public void Debug(string line)
    {
        lock (_lock)
        {
            _debug.Add(line);
        }
    }

    public void Trace(string line)
    {
        lock (_lock)
        {
            _trace.Add(line);
        }
    }
}
