using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The host's hidden browser, as far as a test is concerned: it says whether it is there, opens pages
/// and remembers every one it opened.
/// </summary>
public sealed class FakeBrowser : IPluginBrowser
{
    /// <summary>Why there is no browser here, or empty when there is one.</summary>
    public string Reason { get; init; } = string.Empty;

    /// <summary>Every page opened, in order, open or since disposed.</summary>
    public List<FakePage> Opened { get; } = [];

    /// <summary>What the next pages show, in the order they are opened.</summary>
    public Queue<Action<FakePage>> Scripts { get; } = new();

    /// <summary>
    /// What opening a page throws although the browser says it is available: the host's refusal of a
    /// capability the owner has not approved, or the cancellation its own start timeout throws.
    /// </summary>
    public Exception? OpenFails { get; set; }

    /// <summary>How often a page was asked for, refused or not.</summary>
    public int Opens { get; private set; }

    public bool IsAvailable => Reason.Length == 0;

    public string UnavailableReason => Reason;

    public Task<IPluginBrowserPage> OpenAsync(Uri url, PluginBrowserOptions options, CancellationToken ct = default)
    {
        Opens++;

        if (!IsAvailable)
        {
            // As the host does it: a page asked of a browser that cannot be had.
            throw new InvalidOperationException(Reason);
        }

        if (OpenFails is Exception refused)
        {
            throw refused;
        }

        FakePage page = new(options);

        if (Scripts.Count > 0)
        {
            Scripts.Dequeue()(page);
        }

        page.Visited.Add(url.ToString());
        Opened.Add(page);

        return Task.FromResult<IPluginBrowserPage>(page);
    }
}

/// <summary>One page of the host's browser, answering what a test put on it.</summary>
public sealed class FakePage(PluginBrowserOptions options) : IPluginBrowserPage
{
    private readonly Queue<string> _documents = new();

    public PluginBrowserOptions Options => options;

    /// <summary>Every address navigated to, the one it was opened on first.</summary>
    public List<string> Visited { get; } = [];

    public List<string> FetchedInPage { get; } = [];

    public int Reloads { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Closing throws, as the host's page does when its Chrome has died under it.</summary>
    public bool FailsToClose { get; set; }

    public string ContentType { get; set; } = "text/html";

    public bool Readable { get; set; } = true;

    public string InPageBody { get; set; } = string.Empty;

    public string UserAgent { get; set; } = "a user agent";

    public List<PluginBrowserCookie> Cookies { get; } = [];

    /// <summary>What <see cref="ContentAsync"/> answers, one document per call; the last one repeats.</summary>
    public FakePage Shows(params string[] documents)
    {
        foreach (string document in documents)
        {
            _documents.Enqueue(document);
        }

        return this;
    }

    public Task GotoAsync(Uri url, CancellationToken ct = default)
    {
        Visited.Add(url.ToString());

        return Task.CompletedTask;
    }

    public Task ReloadAsync(CancellationToken ct = default)
    {
        Reloads++;

        return Task.CompletedTask;
    }

    public Task<string> ContentTypeAsync(CancellationToken ct = default)
    {
        return Task.FromResult(ContentType);
    }

    public Task<bool> IsReadableAsync(CancellationToken ct = default)
    {
        return Task.FromResult(Readable);
    }

    public Task<string> FetchTextAsync(Uri url, CancellationToken ct = default)
    {
        FetchedInPage.Add(url.ToString());

        return Task.FromResult(InPageBody);
    }

    public Task<string> UserAgentAsync(CancellationToken ct = default)
    {
        return Task.FromResult(UserAgent);
    }

    public Task WaitForAsync(string selector, CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    public Task<string> ContentAsync(CancellationToken ct = default)
    {
        if (_documents.Count == 0)
        {
            return Task.FromResult(string.Empty);
        }

        string document = _documents.Count == 1 ? _documents.Peek() : _documents.Dequeue();

        return Task.FromResult(document);
    }

    public Task<IReadOnlyList<PluginBrowserCookie>> CookiesAsync(CancellationToken ct = default)
    {
        return Task.FromResult<IReadOnlyList<PluginBrowserCookie>>([.. Cookies]);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;

        if (FailsToClose)
        {
            throw new InvalidOperationException("Target closed.");
        }

        return ValueTask.CompletedTask;
    }
}
