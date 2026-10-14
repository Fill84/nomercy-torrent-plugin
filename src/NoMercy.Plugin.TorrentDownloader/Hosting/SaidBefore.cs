namespace NoMercy.Plugin.TorrentDownloader.Hosting;

/// <summary>
/// What was last said about each item, so a line that comes round every pass is said loudly once.
/// </summary>
/// <remarks>
/// <para>
/// Since S14-01 a finished download whose staging failed is tried again every minute, and the controller's
/// ruling of 14 October 2026 is that a failure repeating on the same item is a Warning the first time and
/// Debug on each identical repeat, until it succeeds or the failure changes. Without it a stuck download
/// put the same Warning into the log once a minute, which buries every other line — the shape 0.3.4's log
/// had, where the line that mattered went unread.
/// </para>
/// <para>
/// <strong>Small and in memory only.</strong> An item is forgotten when it succeeds, and the whole of it is
/// let go if it ever holds more than <see cref="Most"/> — after which a stuck item is said loudly once more,
/// which is the right way to be wrong. A restart forgets it too, and saying a lasting failure once per start
/// is what an owner reading from the top of a log needs.
/// </para>
/// </remarks>
public sealed class SaidBefore
{
    /// <summary>How many items are remembered at most. A server holds far fewer stuck downloads than this.</summary>
    public const int Most = 512;

    private readonly Dictionary<string, string> _said = new(StringComparer.Ordinal);
    private readonly Lock _lock = new();

    /// <summary>
    /// Whether this is news: the first time anything is said about <paramref name="item"/>, or a different
    /// thing from last time. Remembers it either way.
    /// </summary>
    public bool IsNews(string item, string what)
    {
        lock (_lock)
        {
            if (_said.TryGetValue(item, out string? before) && string.Equals(before, what, StringComparison.Ordinal))
            {
                return false;
            }

            if (_said.Count >= Most && !_said.ContainsKey(item))
            {
                _said.Clear();
            }

            _said[item] = what;

            return true;
        }
    }

    /// <summary>The item worked, so the next failure of it is news again.</summary>
    public void Forget(string item)
    {
        lock (_lock)
        {
            _said.Remove(item);
        }
    }
}
