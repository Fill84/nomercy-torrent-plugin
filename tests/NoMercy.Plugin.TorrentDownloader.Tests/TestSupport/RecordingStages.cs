using NoMercy.Plugin.TorrentDownloader.Solver;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// A stage that records the order things happened in, and starts no browser.
/// </summary>
/// <remarks>
/// The order is the behaviour under test. "The stage is created before the
/// browser starts" is not a thing that can be seen from either object
/// afterwards — only from the sequence — so the sequence is what is recorded.
/// </remarks>
public sealed class RecordingStages : IHiddenStageFactory
{
    public List<string> Events { get; } = [];

    public bool CanHideABrowser { get; init; } = true;

    public string? WhyNot { get; init; }

    public IHiddenStage Create()
    {
        if (!CanHideABrowser)
        {
            throw new PlatformNotSupportedException(WhyNot);
        }

        Events.Add("stage created");

        return new RecordingStage(Events);
    }
}

internal sealed class RecordingStage(List<string> events) : IHiddenStage
{
    public string Name => "a recording stage";

    public Task<IBrowserProcess> LaunchAsync(
        string executable,
        IReadOnlyList<string> arguments,
        CancellationToken ct)
    {
        events.Add("browser launched");

        return Task.FromResult<IBrowserProcess>(new FakeBrowserProcess());
    }

    public void Dispose()
    {
        events.Add("stage disposed");
    }
}

/// <summary>A browser that is running until a test says it is not.</summary>
public sealed class FakeBrowserProcess : IBrowserProcess
{
    public bool IsRunning { get; set; } = true;

    public int Port => 9222;

    public void Dispose()
    {
        IsRunning = false;
    }
}

/// <summary>
/// A downloader that holds the first caller inside it until released.
/// </summary>
/// <remarks>
/// The download is the slow part, and it is where a second caller arrives while
/// the first is still working. Blocking there is how two starts at once can be
/// arranged without a sleep and without hoping about timing.
/// </remarks>
public sealed class BlockingBrowserDownloader : IBrowserDownloader
{
    private readonly TaskCompletionSource _started = new();
    private readonly TaskCompletionSource _finish = new();

    public int Downloads { get; private set; }

    /// <summary>Completes once a caller is inside the download.</summary>
    public Task Started => _started.Task;

    /// <summary>Lets the download complete.</summary>
    public void Finish()
    {
        _finish.TrySetResult();
    }

    public async Task<string> DownloadAsync(string folder, CancellationToken ct)
    {
        Downloads++;
        _started.TrySetResult();

        await _finish.Task.WaitAsync(ct);

        string executable = Path.Combine(folder, "chrome.test");
        await File.WriteAllTextAsync(executable, "not really a browser", ct);

        return executable;
    }
}

/// <summary>A downloader that counts how often it was asked.</summary>
public sealed class FakeBrowserDownloader : IBrowserDownloader
{
    public int Downloads { get; private set; }

    public async Task<string> DownloadAsync(string folder, CancellationToken ct)
    {
        Downloads++;

        // A real file, because the install checks that what it recorded is
        // still there — a record pointing at nothing is not an install.
        string executable = Path.Combine(folder, "chrome.test");
        await File.WriteAllTextAsync(executable, "not really a browser", ct);

        return executable;
    }
}
