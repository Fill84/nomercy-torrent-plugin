using NoMercy.Plugin.TorrentDownloader.Core.Domain;
using NoMercy.Plugin.TorrentDownloader.Core.Ports;

namespace NoMercy.Plugin.TorrentDownloader.Tests.TestSupport;

/// <summary>
/// The server's Add content, standing in for the reflection that reaches it.
/// </summary>
/// <remarks>
/// The import itself is one call through <c>IJobDispatcher</c> by name, which
/// no test can make. What a test can hold the cadence to is the outcome: which
/// show it asked for, with which year, and into which library.
/// </remarks>
public sealed class RecordingImport : IShowImport
{
    /// <summary>Every show it was asked to add, in order.</summary>
    public List<(string Title, int? Year, Library Into)> Added { get; } = [];

    /// <summary>What the providers know the show as, or null for one they do not.</summary>
    public string? Answers { get; set; } = "Dark Matter";

    public Task<string?> AddAsync(string title, int? year, Library into, CancellationToken ct)
    {
        Added.Add((title, year, into));

        return Task.FromResult(Answers);
    }
}
