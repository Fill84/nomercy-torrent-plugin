using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The hub, as far as a test is concerned: it counts what was pushed, and can
/// be told to fail the way a disconnected client makes it fail.
/// </summary>
public sealed class FakeHub : IPluginHubContext
{
    private readonly Lock _lock = new();
    private readonly List<(string Type, object? Payload)> _pushes = [];

    /// <summary>When set, every push throws this instead of succeeding.</summary>
    public Exception? Throws { get; set; }

    public IReadOnlyList<(string Type, object? Payload)> Pushes
    {
        get
        {
            lock (_lock)
            {
                return _pushes.ToArray();
            }
        }
    }

    public Task PushAsync(string type, object? payload)
    {
        if (Throws is not null)
        {
            return Task.FromException(Throws);
        }

        lock (_lock)
        {
            _pushes.Add((type, payload));
        }

        return Task.CompletedTask;
    }

    public Task PushToUserAsync(string userId, string type, object? payload)
    {
        return PushAsync(type, payload);
    }
}
