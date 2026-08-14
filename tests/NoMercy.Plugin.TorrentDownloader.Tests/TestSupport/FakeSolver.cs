using NoMercy.Plugin.TorrentDownloader.Core.Sources;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>A browser that clears challenges, as far as a test is concerned.</summary>
public sealed class FakeSolver(Clearance? clearance = null) : IChallengeSolver
{
    /// <summary>How many times it was asked to clear one.</summary>
    public int Solves { get; private set; }

    public Task<Clearance?> SolveAsync(Uri url, CancellationToken ct)
    {
        Solves++;

        return Task.FromResult(clearance);
    }
}

/// <summary>A browser that can hand over a page.</summary>
public sealed class FakePages(string? body) : IPageSource
{
    /// <summary>Every address it was asked for.</summary>
    public List<Uri> Asked { get; } = [];

    public Task<string?> GetPageAsync(Uri url, CancellationToken ct)
    {
        Asked.Add(url);

        return Task.FromResult(body);
    }
}
