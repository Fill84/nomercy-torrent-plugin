using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Core.Tests.TestSupport;

/// <summary>
/// The pool, in memory, so what was written can be looked at.
/// </summary>
/// <remarks>
/// The real one is SQLite and is tested against a real file of its own. What
/// this stands in for is only the keeping, which lets a pipeline test say what
/// went in and what came back out.
/// </remarks>
public sealed class FakePool : INamePool
{
    private readonly Lock _lock = new();
    private readonly List<PooledName> _names = [];

    public IReadOnlyList<PooledName> Names
    {
        get
        {
            lock (_lock)
            {
                return [.. _names];
            }
        }
    }

    public Task AddAsync(IReadOnlyList<PooledName> names, CancellationToken ct)
    {
        lock (_lock)
        {
            // Keyed as the table is keyed, so a name added twice is one name
            // here as well — a fake that let duplicates through would have a
            // pipeline test pass on behaviour the real store does not have.
            foreach (PooledName name in names)
            {
                _names.RemoveAll(kept => kept.Key == name.Key && kept.Title == name.Title);
                _names.Add(name);
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<PooledName>> ForAsync(IReadOnlyCollection<string> keys, CancellationToken ct)
    {
        lock (_lock)
        {
            return Task.FromResult<IReadOnlyList<PooledName>>(
                [.. _names.Where(name => keys.Contains(name.Key, StringComparer.Ordinal))]);
        }
    }
}
